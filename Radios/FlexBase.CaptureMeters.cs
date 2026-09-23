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
                    _VoltsData,
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
    }
}
