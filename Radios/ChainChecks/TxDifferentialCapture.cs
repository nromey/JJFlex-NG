using System;
using System.Collections.Generic;
using System.Globalization;
using JJTrace;
using System.Diagnostics;

namespace Radios.ChainChecks
{
    /// <summary>
    /// Reads the watched meters off a live radio into a
    /// <see cref="TxDifferential.TxRunSample"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept apart from <see cref="TxDifferential"/> on purpose. The comparison
    /// is pure decision logic over plain data and is tested exhaustively
    /// without a radio in the room; this is the part that touches FlexLib and
    /// cannot be. Mixing them would make the half that matters most
    /// untestable, which is how a diagnostic ends up trusted and unverified.
    /// </para>
    /// <para>
    /// <b>The same set is read every time, whatever the radio is doing.</b> A
    /// meter that is absent or has never reported comes back marked as such
    /// rather than omitted, because the comparison must be able to tell "read
    /// differently" from "was not read" — and only a fixed set makes that
    /// distinguishable.
    /// </para>
    /// </remarks>
    public static class TxDifferentialCapture
    {
        /// <summary>
        /// Capture one run's worth of readings.
        /// </summary>
        /// <param name="rig">The radio. Null yields a run with nothing reported.</param>
        /// <param name="kind">Which half of the differential this is.</param>
        /// <remarks>
        /// Call this WHILE transmitting, at the end of the run rather than the
        /// start. The transmit meters appear with the transmit chain and update
        /// a few times a second (measured on the bench 8600, 2026-08-20: eleven
        /// meters with no client connected, thirty-five once the chain is up),
        /// so a reading taken at key-down is a reading of the moment before
        /// anything happened.
        /// </remarks>
        public static TxDifferential.TxRunSample Capture(FlexBase rig, TxDifferential.RunKind kind)
        {
            var meters = new List<TxDifferential.MeterSample>(TxDifferential.Watched.Length);
            MeterInventory inv = SafeInventory(rig);

            foreach (string name in TxDifferential.Watched)
            {
                MeterReading r = null;
                try { r = inv?.Find(name); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("TxDifferentialCapture: could not read " + name
                        + " — " + ex.Message, TraceLevel.Warning);
                }

                // Absent and never-reported are both "not reported". They are
                // different facts about the radio, but they are the same fact
                // for a comparison: there is no number here to compare.
                bool reported = r != null && r.HasReading;
                meters.Add(new TxDifferential.MeterSample(
                    name, reported ? r.Value : 0.0, reported ? r.Units.ToString() : "", reported));
            }

            var sample = TxDifferential.TxRunSample.Measured(
                kind, DateTime.UtcNow, meters,
                SafeFrequency(rig), SafeMode(rig), SafeAntenna(rig));

            // Trace what was captured, because a differential that later reads
            // oddly is worth being able to reconstruct from a log that already
            // exists rather than by asking the operator to run it again.
            Tracing.TraceLine("TxDifferentialCapture: " + kind + " run at "
                + sample.Frequency + " " + sample.Mode + " on " + sample.Antenna
                + " — " + Describe(meters), TraceLevel.Info);

            return sample;
        }

        private static string Describe(IReadOnlyList<TxDifferential.MeterSample> meters)
        {
            var parts = new List<string>(meters.Count);
            foreach (TxDifferential.MeterSample m in meters) parts.Add(m.Describe());
            return string.Join(", ", parts);
        }

        private static MeterInventory SafeInventory(FlexBase rig)
        {
            try { return rig?.MeterInventory; }
            catch { return null; }
        }

        /// <remarks>
        /// Conditions travel with the measurement so a reader can reproduce
        /// them rather than take our word (#217), and because a reading with no
        /// recorded antenna port is a number a support engineer cannot use
        /// (#188). Each is read defensively: a condition we cannot name must
        /// say so, never guess, and never take the whole capture down with it.
        /// </remarks>
        private static string SafeFrequency(FlexBase rig)
        {
            try
            {
                ulong hz = rig?.TXFrequency ?? 0UL;
                return hz == 0UL ? "not reported"
                    : (hz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture) + " MHz";
            }
            catch { return "could not be read"; }
        }

        private static string SafeMode(FlexBase rig)
        {
            try
            {
                string m = rig?.TXMode;
                return string.IsNullOrWhiteSpace(m) ? "not reported" : m;
            }
            catch { return "could not be read"; }
        }

        private static string SafeAntenna(FlexBase rig)
        {
            try
            {
                string a = rig?.TXAntennaName;
                return string.IsNullOrWhiteSpace(a) ? "not reported" : a;
            }
            catch { return "could not be read"; }
        }

        /// <summary>
        /// Whether the transmit conditioning chain is doing anything right now.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Feeds <see cref="TxProbeSet.ExplainSplit"/>, which must not name a
        /// setting without consulting it. The gate, RNNoise and spectral
        /// subtraction ALL default to off, so an operator arriving at this
        /// build for the first time has an entirely bypassed chain — and
        /// telling them to go and turn it off would look like an answer while
        /// being none.
        /// </para>
        /// <para>
        /// Returns null when it cannot be determined, and the explanation
        /// hedges rather than guessing. Not knowing is a third state.
        /// </para>
        /// </remarks>
        public static bool? ConditioningActive(FlexBase rig)
        {
            try
            {
                JJPortaudio.TxAudioConditioner c = rig?.TxConditioner;
                if (c == null) return null;
                return c.Gate.Enabled || c.NoiseReducer != null;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxDifferentialCapture: could not read the conditioning state — "
                    + ex.Message, TraceLevel.Warning);
                return null;
            }
        }
    }
}
