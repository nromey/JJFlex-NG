using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The half of <see cref="CaptureMeterSet"/> that lives on the rig: the
    /// per-radio coalescer and the one line that writes it out.
    ///
    /// <para><b>Why a partial file rather than more of FlexBase.cs.</b> Sprint
    /// 45 Track H shared <c>FlexBase.cs</c> with Track G3, which was live in the
    /// connect, profile and station regions on another worktree at the same
    /// time. A textual conflict there costs a merge, and git cannot warn about
    /// one. So this track added one line to <c>PATempDataHandler</c> and put
    /// everything else here.</para>
    /// </summary>
    public partial class FlexBase
    {
        /// <summary>
        /// This radio's PA-temperature window. See <see cref="CaptureMeterSet"/>
        /// for what is recorded and why it is not the meter stream.
        /// </summary>
        private readonly CaptureMeterSet _captureMeters = new CaptureMeterSet();

        /// <summary>
        /// THE ONE SELECTION (#566): the meter names the operator's recorded
        /// set holds, as the alarm subsystem last pushed them, or null while
        /// nothing has pushed one. Replaces the list <c>CaptureMeterSet</c>
        /// used to carry.
        /// </summary>
        private HashSet<string> _captureSelection;
        private readonly object _captureSelectionGate = new object();
        private bool _captureSelectionMissingTraced;

        /// <summary>
        /// The operator's recorded-meter set, as the radio names the meters.
        /// Called by <c>OperatorAlarmHost</c> when the alarm service attaches
        /// and whenever its recorded set changes; a test calls it directly.
        /// Thread-safe: the set is replaced whole under a short lock, and the
        /// meter thread reads it under the same lock.
        /// </summary>
        public void SetCaptureSelection(IEnumerable<string> meterNames)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (meterNames != null) foreach (string n in meterNames) if (!string.IsNullOrEmpty(n)) set.Add(n);

            // When the change moves the window from one driver to the other —
            // temperature ticked or unticked while a supply meter is recorded,
            // or the last recorded meter leaving — the open window is CUT under
            // the selection it ran under, so a sample the radio sent is written
            // rather than dropped or finished by a handler that was not asked
            // for it (Astra's Track IJK2 review, blocker 2; the reasoning is
            // on CaptureMeterSet.CloseIfOpen). The volts snapshot and the
            // temperature flag describe the OLD selection, because the line
            // describes that window. A change that keeps the same driver — a
            // power meter added, the other supply meter ticked — cuts nothing.
            HashSet<string> old;
            lock (_captureSelectionGate)
            {
                old = _captureSelection;
                _captureSelection = set;
            }
            if (captureDriverOf(old) == captureDriverOf(set)) return;
            try
            {
                string cut = _captureMeters.CloseIfOpen(
                    readSupplyVoltage(old),
                    Environment.TickCount,
                    temperatureSelected: old != null && old.Contains(CaptureMeterSet.PaTemperatureMeterName));
                if (cut != null) Tracing.TraceRecord(CaptureMeterSet.CaptureMetersRecord, cut, TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("SetCaptureSelection: could not close the open capture window — " + ex.Message, TraceLevel.Warning);
            }
        }

        /// <summary>Which handler drives the <c>captureMeters:</c> window under a selection.</summary>
        private enum CaptureDriver { None, Temperature, SupplyVoltage }

        /// <summary>
        /// The temperature handler while PATEMP is selected; the supply meter's
        /// handler while a supply meter is and PATEMP is not; nothing
        /// otherwise. The same rule the two handlers apply, written once.
        /// </summary>
        private static CaptureDriver captureDriverOf(HashSet<string> selection)
        {
            if (selection == null) return CaptureDriver.None;
            if (selection.Contains(CaptureMeterSet.PaTemperatureMeterName)) return CaptureDriver.Temperature;
            return supplyVoltageSelected(selection) ? CaptureDriver.SupplyVoltage : CaptureDriver.None;
        }

        /// <summary>
        /// Whether a meter is in the recorded set. With NO selection ever
        /// pushed — the alarm subsystem not attached — nothing is recorded, and
        /// the trace says so once: a capture that silently records nothing is
        /// the #494 failure again, and an absence must be visible.
        /// </summary>
        private bool isSelectedForCapture(string meterName, bool traceIfNoSelection = true)
        {
            lock (_captureSelectionGate)
            {
                if (_captureSelection == null)
                {
                    if (traceIfNoSelection && !_captureSelectionMissingTraced)
                    {
                        _captureSelectionMissingTraced = true;
                        Tracing.TraceLine("recordCaptureMeters: no recorded-meter selection has been pushed to this rig, so the "
                            + "temperature window records nothing until the alarm subsystem attaches (#566)", TraceLevel.Warning);
                    }
                    return false;
                }
                return _captureSelection.Contains(meterName);
            }
        }

        /// <summary>
        /// The supply-voltage meter the voltage field follows: the preferred
        /// one when it is selected and published, else the first other
        /// selected meter the inventory holds in volts, else null for "not
        /// selected". Inventory order, so two operators with the same radio
        /// and selection get the same meter.
        /// </summary>
        private static MeterReading selectedSupplyMeter(MeterInventory inv, HashSet<string> selection)
        {
            if (selection == null || selection.Count == 0) return null;

            if (selection.Contains(CaptureMeterSet.PreferredSupplyVoltageMeterName))
            {
                MeterReading preferred = inv.Find(CaptureMeterSet.PreferredSupplyVoltageMeterName);
                if (preferred != null) return preferred;
            }
            foreach (string name in selection)
            {
                MeterReading m = inv.Find(name);
                if (m != null && m.Units == Flex.Smoothlake.FlexLib.MeterUnits.Volts) return m;
            }
            return null;
        }

        /// <summary>True when any selected meter is a supply-voltage meter by name, whether or not this radio publishes it.</summary>
        private bool supplyVoltageSelected()
        {
            HashSet<string> selection;
            lock (_captureSelectionGate) selection = _captureSelection;
            return supplyVoltageSelected(selection);
        }

        private static bool supplyVoltageSelected(HashSet<string> selection)
        {
            if (selection == null) return false;
            foreach (string name in selection)
                if (name.StartsWith("+13.8", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Record one PA temperature reading into the ordinary diagnostic
        /// capture. Called from <c>PATempDataHandler</c>, on FlexLib's meter
        /// packet thread, at meter rate — so the common path is an accumulate
        /// under one short lock and nothing else, and the file write happens at
        /// most once a window.
        ///
        /// <para><b>Unconditional, at Info.</b> Not gated on
        /// <c>RecordMeterStream</c>, which is the gate that made temperature
        /// invisible in every capture anybody ever took (the measurement is in
        /// <see cref="CaptureMeterSet"/>'s remarks). Not gated on transmit
        /// either: the resting window is thirty seconds and it is what gives a
        /// transmit reading a baseline to be read against.</para>
        /// </summary>
        private void recordCaptureMeters(float celsius)
        {
            try
            {
                // Recorded only while the operator's set holds PA temperature
                // (#566). A radio with no alarm file yet is seeded with it, so
                // the ordinary case records as it always did. With temperature
                // unticked the window is driven by the supply meter instead —
                // see recordCaptureMetersFromVolts.
                if (!isSelectedForCapture(CaptureMeterSet.PaTemperatureMeterName)) return;
                string line = _captureMeters.Report(
                    celsius,
                    readSupplyVoltage(),
                    Transmit || _tuneCycleActive,
                    Environment.TickCount);
                // A data record with its kind (#625): the file introduces the
                // line where it first appears, in CaptureMeterSet's own words.
                if (line != null) Tracing.TraceRecord(CaptureMeterSet.CaptureMetersRecord, line, TraceLevel.Info);
            }
            catch (Exception ex)
            {
                // A meter reading must never be able to take a radio down. This
                // runs on FlexLib's packet thread, where an escaping exception
                // has no owner at all.
                Tracing.TraceLine("recordCaptureMeters: " + ex.Message, TraceLevel.Warning);
            }
        }

        /// <summary>
        /// The supply meter's reading arrived. When PA temperature is NOT in
        /// the recorded set but a supply meter is, this drives the
        /// <c>captureMeters:</c> window on the supply meter's own cadence, so
        /// the voltage history is still written once a window. Called from
        /// <c>VoltsDataHandler</c>, on FlexLib's meter packet thread.
        ///
        /// <para><b>Astra's Track IJK review, blocker 4 — introduced by the
        /// one-selection fold.</b> <see cref="recordCaptureMeters"/> returned
        /// unless PATEMP was selected, and it was the only thing that fed the
        /// window, so a selection of supply voltage without temperature
        /// recorded NOTHING periodically while the dialog said the supply
        /// meter was recorded; a terminal flush on the drop could not restore
        /// the samples in between. While temperature IS selected this does
        /// nothing: the temperature handler owns the window, and two drivers
        /// would close it twice.</para>
        /// </summary>
        private void recordCaptureMetersFromVolts()
        {
            try
            {
                if (isSelectedForCapture(CaptureMeterSet.PaTemperatureMeterName)) return;
                if (!supplyVoltageSelected()) return;
                string line = _captureMeters.ReportWithoutTemperature(
                    readSupplyVoltage(),
                    Transmit || _tuneCycleActive,
                    Environment.TickCount);
                if (line != null) Tracing.TraceRecord(CaptureMeterSet.CaptureMetersRecord, line, TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("recordCaptureMetersFromVolts: " + ex.Message, TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Close the open temperature window and RENDER it, marked with why it
        /// was cut short. Returns the line; it writes nothing.
        ///
        /// <para><b>Collection and the trace call are split, and that split is
        /// #618's fix.</b> H2 added a synchronous flush on the drop arm so a
        /// connection lost inside the first second still recorded a temperature
        /// line (#598) — and it wrote <c>partial=connection_dropped</c> BEFORE
        /// asking whether this removal was already claimed, so a repeat notice
        /// could stamp a false drop line into the fresh standing log the first
        /// archive had just started.</para>
        ///
        /// <para><b>Merely moving the old call after the claim would not have
        /// been enough.</b> The claim runs on FlexLib's removal thread and the
        /// archive runs on a worker; a session replacement can land in between, and
        /// an unqualified <c>Tracing.TraceLine</c> writes to whatever sink is
        /// current at the moment it runs. So the window is rendered here, by the
        /// removal that won the claim, and travels as DATA to the trace
        /// boundary, which writes it into the accepted session's own sink or
        /// discards it. A rejected duplicate emits no such record anywhere, and
        /// an old request never emits one in a successor's log.</para>
        ///
        /// <para>Internal rather than private so a test can drive it on a real
        /// rig without reflection; there is one production caller and it is the
        /// drop path.</para>
        ///
        /// <para><b>Returned as a record with its kind, not a bare string</b>
        /// (#625). This window is written into the archived file as a terminal
        /// record by the trace boundary, and the boundary must not have to
        /// guess what it is: the writer says so, here, and the kind travels
        /// with the text through <c>CaptureArchiveRequest</c> to the sink,
        /// which introduces it if no <c>captureMeters:</c> line has spoken for
        /// itself in that part yet.</para>
        ///
        /// <para><b>Read <c>state=</c> on a flushed line with #596 in mind.</b>
        /// An open window carries the state it earned from its own samples, but
        /// an EMPTY flush has nothing to go on and asks
        /// <see cref="Transmit"/> — which is written only from the radio's Mox
        /// message, so a radio that has just died leaves it reading true
        /// indefinitely. On a drop that is exactly the moment we are in. The
        /// number of samples is the honest field here; the state word on an
        /// <c>n=0</c> line becomes trustworthy the day #596 clears the flag on
        /// this same path, which is a ruling rather than a tidy-up and is not
        /// this track's to make.</para>
        /// </summary>
        internal TraceRecord collectCaptureMeterFlush(string reason)
        {
            try
            {
                return new TraceRecord(
                    _captureMeters.Flush(
                        readSupplyVoltage(),
                        Transmit || _tuneCycleActive,
                        reason,
                        Environment.TickCount,
                        // Quietly: this runs on the transport thread on the
                        // drop path, and the missing-selection line, if it is
                        // owed at all, belongs to the handlers that record.
                        temperatureSelected: isSelectedForCapture(CaptureMeterSet.PaTemperatureMeterName, traceIfNoSelection: false)),
                    CaptureMeterSet.CaptureMetersRecord);
            }
            catch (Exception ex)
            {
                // The drop path must survive anything. A radio has just died;
                // an exception here would take the archive with it.
                // Deferred: this runs on FlexLib's transport thread, on the
                // drop path, which must not wait on the trace gate.
                Tracing.TraceLineDeferred("collectCaptureMeterFlush: " + ex.Message, TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// What is known about supply voltage right now — asked of the meter
        /// inventory, which is the thing that knows whether the meter exists,
        /// whether it has ever reported, and when (#597).
        ///
        /// <para>The first build decided presence by testing the cached reading
        /// against zero, so a radio that genuinely read zero volts and a radio
        /// with no such meter produced the same word. The inventory answers the
        /// actual question, and a zero can now be said out loud — which on a
        /// transmitting radio is the most interesting thing this field could
        /// ever carry.</para>
        /// </summary>
        private SupplyVoltage readSupplyVoltage()
        {
            HashSet<string> selection;
            lock (_captureSelectionGate) selection = _captureSelection;
            return readSupplyVoltage(selection);
        }

        /// <summary>The same, under a selection the caller names — the one a
        /// window being cut ran under, which may no longer be current.</summary>
        private SupplyVoltage readSupplyVoltage(HashSet<string> selection)
        {
            // The selection decides WHICH supply meter, and whether one at all
            // (#566): no supply meter in the recorded set is a choice, written
            // as not-selected; a selected one the radio does not publish is
            // no-meter, which on a 6300 is a finding (#597).
            if (!supplyVoltageSelected(selection)) return SupplyVoltage.NotSelected();

            MeterInventory inv = MeterInventory;
            if (inv == null || inv.Count == 0) return SupplyVoltage.Unknown();

            MeterReading m = selectedSupplyMeter(inv, selection);
            if (m == null) return SupplyVoltage.NoMeter();
            if (!m.HasReading) return SupplyVoltage.NoSample();
            return SupplyVoltage.Reading(m.Value, m.Age);
        }
    }
}
