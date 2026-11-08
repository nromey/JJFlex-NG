using System;
using System.Diagnostics;
using System.Globalization;
using JJTrace;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The power amplifier's temperature across one keyed check stage: read at
    /// key-down and again just before the carrier drops, reported in the
    /// stage's evidence, and traced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read, report, trace, and nothing else.</b> Ruled by Noel 2026-08-26
    /// (<i>"always can monitor PA temp too as a safety as well"</i>), with the
    /// instruction to invent no threshold: what temperature is concerning on
    /// these radios has never been measured here. So nothing in this type
    /// judges a reading, and nothing that judges a check reads this type. A
    /// missing reading is "not reported", never a zero and never a failure,
    /// and it changes no verdict, no finding and no refusal.
    /// </para>
    /// <para>
    /// <b>Two readings, not one.</b> Heat is slow and builds across checks run
    /// one after another, each within its own budget. A start and an end per
    /// keyed span let a bench sitting read the rise, which is what a threshold
    /// will eventually be set from.
    /// </para>
    /// </remarks>
    public sealed class PaTemperatureSpan
    {
        /// <summary>The radio's power amplifier temperature meter.</summary>
        public const string Meter = "PATEMP";

        /// <summary>The stage this span belongs to, for the trace.</summary>
        public string Stage { get; }

        /// <summary>The reading when the radio confirmed it was keyed.</summary>
        public TxTuneProbe.Reading AtKeyDown { get; }

        /// <summary>The reading just before the carrier was dropped;
        /// "not reported" until <see cref="End"/> has run.</summary>
        public TxTuneProbe.Reading AtKeyUp { get; private set; } = TxTuneProbe.Reading.Missing(Meter);

        /// <summary>True once <see cref="End"/> has run.</summary>
        public bool Ended { get; private set; }

        private PaTemperatureSpan(string stage, TxTuneProbe.Reading atKeyDown)
        {
            Stage = stage ?? "";
            AtKeyDown = atKeyDown;
        }

        /// <summary>
        /// Start a span at key-down. <paramref name="read"/> is asked once; if
        /// it throws, the reading is "not reported". Traces the reading.
        /// </summary>
        public static PaTemperatureSpan Begin(string stage, Func<TxTuneProbe.Reading> read)
        {
            var span = new PaTemperatureSpan(stage, SafeRead(read));
            Tracing.TraceLine("PaTemperature: " + span.Stage + " key-down, "
                + Describe(span.AtKeyDown), TraceLevel.Info);
            return span;
        }

        /// <summary>
        /// Take the key-up reading. Call while still keyed, just before the
        /// carrier drops. Only the first call counts. Never throws: this runs
        /// in an unkey <c>finally</c>.
        /// </summary>
        public void End(Func<TxTuneProbe.Reading> read)
        {
            if (Ended) return;
            AtKeyUp = SafeRead(read);
            Ended = true;
            try
            {
                Tracing.TraceLine("PaTemperature: " + Stage + " key-up, "
                    + Describe(AtKeyUp) + Rise(" ", ""), TraceLevel.Info);
            }
            catch
            {
                // A trace failure must not replace whatever happened in the stage.
            }
        }

        /// <summary>
        /// One plain line for the stage's evidence: both readings, each
        /// "not reported" when it did not arrive, and the change when both did.
        /// </summary>
        public string EvidenceLine()
            => "PA temperature (PATEMP, read only; nothing is judged on it): at key-down "
               + Value(AtKeyDown) + ", at key-up " + (Ended ? Value(AtKeyUp) : "not read")
               + Rise(", a change of ", "") + ".";

        /// <summary>The span's line with a line break, or nothing when there
        /// is no span (the stage never keyed).</summary>
        public static string EvidenceLines(PaTemperatureSpan span)
            => span == null ? "" : span.EvidenceLine() + Environment.NewLine;

        /// <summary>
        /// The radio's PA temperature now. Never throws; a meter that is
        /// absent or has not reported is <see cref="TxTuneProbe.Reading.Missing"/>.
        /// </summary>
        public static TxTuneProbe.Reading ReadFrom(FlexBase rig)
        {
            try
            {
                MeterReading r = rig?.MeterInventory?.Find(Meter);
                return r != null && r.HasReading
                    ? TxTuneProbe.Reading.Got(Meter, r.Value, r.Units.ToString())
                    : TxTuneProbe.Reading.Missing(Meter);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("PaTemperature: could not read " + Meter + " — " + ex.Message,
                                  TraceLevel.Warning);
                return TxTuneProbe.Reading.Missing(Meter);
            }
        }

        private static TxTuneProbe.Reading SafeRead(Func<TxTuneProbe.Reading> read)
        {
            try { return read == null ? TxTuneProbe.Reading.Missing(Meter) : read(); }
            catch { return TxTuneProbe.Reading.Missing(Meter); }
        }

        private static string Value(TxTuneProbe.Reading r)
            => r.Reported
                ? r.Value.ToString("0.##", CultureInfo.InvariantCulture) + (r.Units.Length > 0 ? " " + r.Units : "")
                : "not reported";

        private static string Describe(TxTuneProbe.Reading r) => r.ToString();

        /// <summary>The change from key-down to key-up, signed, when both
        /// readings arrived in the same units; otherwise nothing.</summary>
        private string Rise(string lead, string tail)
        {
            if (!Ended || !AtKeyDown.Reported || !AtKeyUp.Reported) return "";
            if (!string.Equals(AtKeyDown.Units, AtKeyUp.Units, StringComparison.Ordinal)) return "";
            double d = AtKeyUp.Value - AtKeyDown.Value;
            return lead + (d >= 0 ? "+" : "") + d.ToString("0.##", CultureInfo.InvariantCulture)
                 + (AtKeyUp.Units.Length > 0 ? " " + AtKeyUp.Units : "") + tail;
        }
    }
}
