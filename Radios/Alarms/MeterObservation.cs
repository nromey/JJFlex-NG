#nullable enable
using System;

namespace Radios.Alarms
{
    /// <summary>Whether a delivered value can be judged as a reading.</summary>
    public enum ObservationValidity
    {
        /// <summary>A finite value. May still be out of the advertised range — see <see cref="MeterObservation.OutOfRange"/>.</summary>
        Valid,

        /// <summary>NaN or infinite. Not zero, not a reading.</summary>
        NonFinite,

        /// <summary>The meter's own documented floor or sentinel (the -150 dBFS an
        /// SC_MIC copy sits at while it has never reported). Not zero, not a reading.</summary>
        Sentinel,
    }

    /// <summary>Where an observation came from. Only <see cref="Measured"/> reaches the live engine.</summary>
    public enum ObservationProvenance
    {
        /// <summary>A real callback from the connected radio.</summary>
        Measured,

        /// <summary>A synthetic value the Preview warning built. Exercises the output path; never judged.</summary>
        Preview,

        /// <summary>A recorded value played back through the engine by a test or a replay harness.</summary>
        Replay,
    }

    /// <summary>
    /// One meter callback, frozen at receipt: identity, typed value, validity,
    /// monotonic and UTC receipt time, connection generation and sequence,
    /// captured together on the meter thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a value type built once and never re-read.</b> The inventory's
    /// <see cref="MeterReading"/> exposes <c>Value</c>, <c>LastUpdateUtc</c> and
    /// <c>UpdateCount</c> as three separate volatile reads, which is fine for
    /// display and not an atomic snapshot. A freshness decision made from
    /// three reads can pair a new value with an old stamp. So the live
    /// attachment builds one of these in the callback and everything
    /// downstream reasons about the observation, not the cache.
    /// </para>
    /// <para>
    /// <b>Fresh means received through this connection</b>, not proven newly
    /// acquired at the sensor: FlexLib coalesces queued packets per meter and
    /// the callback carries no acquisition timestamp (design section 2).
    /// <see cref="SensorTimestampBasis"/> says so in every record.
    /// </para>
    /// <para>
    /// <b>Equal values get new sequences.</b> Two deliveries of 25.703125 C two
    /// seconds apart are two observations, and the second one is the one that
    /// proves the feed is alive.
    /// </para>
    /// </remarks>
    public readonly record struct MeterObservation(
        MeterDescriptor Meter,
        float Value,
        ObservationValidity Validity,
        bool OutOfRange,
        long Sequence,
        long ReceiptMonotonicMs,
        DateTime ReceiptUtc,
        int ConnectionGeneration,
        ObservationProvenance Provenance)
    {
        /// <summary>
        /// The one honest statement about sensor time: the callback does not
        /// supply one, so it is recorded as null with this basis.
        /// </summary>
        public const string SensorTimestampBasis =
            "not supplied by the meter callback; receipt time is the only time known";

        /// <summary>True when this value can be judged against a threshold.</summary>
        public bool IsValid => Validity == ObservationValidity.Valid;

        /// <summary>Age at <paramref name="nowMs"/> on the same monotonic clock, never negative.</summary>
        public double AgeSeconds(long nowMs) => Math.Max(0, nowMs - ReceiptMonotonicMs) / 1000.0;

        /// <summary>Build a measured observation, classifying validity and range here so no caller can forget to.</summary>
        public static MeterObservation Measured(MeterDescriptor meter, float value, long sequence,
            long receiptMonotonicMs, DateTime receiptUtc, int connectionGeneration, float? sentinel)
            => Build(meter, value, sequence, receiptMonotonicMs, receiptUtc, connectionGeneration,
                sentinel, ObservationProvenance.Measured);

        /// <summary>A replayed observation: recorded timing, recorded value, marked as replay.</summary>
        public static MeterObservation Replayed(MeterDescriptor meter, float value, long sequence,
            long receiptMonotonicMs, DateTime receiptUtc, int connectionGeneration, float? sentinel)
            => Build(meter, value, sequence, receiptMonotonicMs, receiptUtc, connectionGeneration,
                sentinel, ObservationProvenance.Replay);

        /// <summary>A preview observation. It carries the marker that keeps it out of the engine.</summary>
        public static MeterObservation PreviewOf(MeterDescriptor meter, float value, long receiptMonotonicMs,
            DateTime receiptUtc, int connectionGeneration)
            => Build(meter, value, 0, receiptMonotonicMs, receiptUtc, connectionGeneration,
                null, ObservationProvenance.Preview);

        private static MeterObservation Build(MeterDescriptor meter, float value, long sequence,
            long receiptMonotonicMs, DateTime receiptUtc, int connectionGeneration, float? sentinel,
            ObservationProvenance provenance)
        {
            ObservationValidity validity;
            if (!float.IsFinite(value)) validity = ObservationValidity.NonFinite;
            else if (sentinel.HasValue && value == sentinel.Value) validity = ObservationValidity.Sentinel;
            else validity = ObservationValidity.Valid;

            bool outOfRange = validity == ObservationValidity.Valid && meter.IsOutOfRange(value);
            return new MeterObservation(meter, value, validity, outOfRange, sequence,
                receiptMonotonicMs, receiptUtc, connectionGeneration, provenance);
        }
    }
}
