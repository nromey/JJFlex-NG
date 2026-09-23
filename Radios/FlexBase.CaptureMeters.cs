using System;
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
                string line = _captureMeters.Report(
                    celsius,
                    readSupplyVoltage(),
                    Transmit || _tuneCycleActive,
                    Environment.TickCount);
                if (line != null) Tracing.TraceLine(line, TraceLevel.Info);
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
        /// Close the open temperature window and write it out, marked with why
        /// it was cut short.
        ///
        /// <para>Called synchronously from the connection-drop arm of
        /// <c>apiRadioRemovedHandler</c>, BEFORE the seal is queued, so the last
        /// readings are in the file the seal is about to zip. Emitting only on a
        /// natural window close meant a drop inside the first second saved no
        /// temperature at all, and every later drop lost the final partial
        /// window (#598).</para>
        ///
        /// <para>Internal rather than private so a test can drive it on a real
        /// rig without reflection; there is one production caller and it is the
        /// drop path.</para>
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
        internal void flushCaptureMeters(string reason)
        {
            try
            {
                string line = _captureMeters.Flush(
                    readSupplyVoltage(),
                    Transmit || _tuneCycleActive,
                    reason,
                    Environment.TickCount);
                if (line != null) Tracing.TraceLine(line, TraceLevel.Info);
            }
            catch (Exception ex)
            {
                // The drop path must survive anything. A radio has just died;
                // an exception here would take the seal with it.
                Tracing.TraceLine("flushCaptureMeters: " + ex.Message, TraceLevel.Warning);
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
            MeterInventory inv = MeterInventory;
            if (inv == null || inv.Count == 0) return SupplyVoltage.Unknown();

            MeterReading m = inv.Find(CaptureMeterSet.SupplyVoltageMeterName);
            if (m == null) return SupplyVoltage.NoMeter();
            if (!m.HasReading) return SupplyVoltage.NoSample();
            return SupplyVoltage.Reading(m.Value, m.Age);
        }
    }
}
