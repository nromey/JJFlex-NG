using System;
using System.Collections.Generic;
using System.Globalization;

namespace Radios
{
    /// <summary>
    /// The meters an ordinary diagnostic capture records per tick — and the one
    /// place that list is written down.
    ///
    /// <para><b>THE MEASUREMENT THAT PUT THIS HERE (2026-09-22).</b> Two records
    /// disagreed about whether PA temperature reaches a capture, so a real
    /// capture on disk was asked. <c>trace-20260907-080956-clean_exit.zip</c> is
    /// a genuine <c>Ctrl+J</c>, <c>Ctrl+D</c> capture — its own header line says
    /// <c>reason=Ctrl+J Ctrl+D level=Verbose</c> — taken across a real
    /// transmission on the bench 8600. It carries <b>258</b> <c>txMeters:</c>
    /// lines, every one of them with <c>reflW=</c> on it. It carries
    /// <b>zero</b> occurrences of <c>paTemp:</c> and <b>zero</b> of
    /// <c>PATEMP</c>. The 193629 capture the same evening: 194 and zero. So
    /// #494's bench note was right and still is.</para>
    ///
    /// <para><b>And the reason it was absent was not the one written down.</b>
    /// <c>PATempDataHandler</c>'s comment blamed Verbose lines being dropped at
    /// Normal detail — true of the old raw lines, and no longer the mechanism.
    /// The handler reports through <see cref="MeterTraceStream"/>, which is
    /// gated on <c>DiagnosticsConfig.RecordMeterStream</c>, an opt-in switch
    /// that is <b>off by default and has nothing to do with the detail
    /// level</b>. That is why the capture above was silent at Verbose, and why
    /// the only captures on this machine that do carry <c>paTemp:</c> are the
    /// handful from 2026-08-23 to 08-26 when somebody had the switch on. An
    /// operator asked to "run a capture while you transmit" turns the switch on
    /// never, because nobody tells them it exists.</para>
    ///
    /// <para><b>So temperature moves onto the unconditional path</b> — the one
    /// <c>txMeters:</c> already uses for forward and reflected power:
    /// <c>Tracing.TraceLine(..., TraceLevel.Info)</c>, which Normal detail
    /// carries (<c>DiagnosticsConfig.TraceLevel</c> maps Normal to Info). No
    /// switch, no setting, no instruction to give a tester.</para>
    ///
    /// <para><b>#566 REPLACES THIS LIST.</b> Ruled by Noel 2026-09-22: the same
    /// operator-chosen meter selection feeds the alarms and the capture, so an
    /// alarm on a meter means that meter is recorded, and a meter can be
    /// recorded without an alarm on it. Until that subsystem exists the set is
    /// hardcoded — forward power, reflected power, PA temperature, and supply
    /// voltage where the radio publishes one — and it is hardcoded HERE, in one
    /// readable list, so the subsystem has one thing to replace rather than a
    /// handler-by-handler hunt. Today's set being an accident of which handlers
    /// somebody happened to instrument is the exact complaint #566 records.</para>
    /// </summary>
    public sealed class CaptureMeterSet
    {
        /// <summary>
        /// One meter in the recorded set: what the radio calls it, what we call
        /// it in the trace, and which line carries it.
        /// </summary>
        public readonly struct RecordedMeter
        {
            public RecordedMeter(string radioMeterName, string traceField, string unit, string carriedBy)
            {
                RadioMeterName = radioMeterName;
                TraceField = traceField;
                Unit = unit;
                CarriedBy = carriedBy;
            }

            /// <summary>The radio's own name for the meter, as the meter
            /// inventory prints it.</summary>
            public string RadioMeterName { get; }

            /// <summary>The field name in the trace line.</summary>
            public string TraceField { get; }

            /// <summary>Unit, for a reader of the line.</summary>
            public string Unit { get; }

            /// <summary>Which trace line carries it today.</summary>
            public string CarriedBy { get; }
        }

        /// <summary>The <c>txMeters:</c> line, written by
        /// <c>FlexBase.traceTxMeters</c> while transmitting or tuning.</summary>
        public const string TxMetersLine = "txMeters:";

        /// <summary>The <c>captureMeters:</c> line, written by this class.</summary>
        public const string CaptureMetersLine = "captureMeters:";

        /// <summary>
        /// THE RECORDED SET. Four meters, and the two lines that carry them.
        /// Forward and reflected power already reach an ordinary capture on the
        /// <c>txMeters:</c> line and are listed here because a list of what a
        /// capture records that leaves out half of it is not a list — it is a
        /// second place to look. Nothing re-emits them; #566 replaces the whole
        /// table with the operator's selection.
        /// </summary>
        public static readonly IReadOnlyList<RecordedMeter> Recorded = new[]
        {
            new RecordedMeter("FWDPWR", "fwdW",    "watts",   TxMetersLine),
            new RecordedMeter("REFPWR", "reflW",   "watts",   TxMetersLine),
            new RecordedMeter("PATEMP", "paTemp",  "C",       CaptureMetersLine),
            new RecordedMeter("+13.8A", "volts",   "V",       CaptureMetersLine),
        };

        /// <summary>
        /// How often a <c>captureMeters:</c> line is written while the radio is
        /// transmitting or tuning. One a second, matching <c>txMeters:</c> so the
        /// two interleave readably.
        /// </summary>
        public const int TransmitWindowMs = 1000;

        /// <summary>
        /// How often one is written the rest of the time. Thirty seconds.
        ///
        /// <para>Not zero, and not one second. Not zero because the baseline
        /// matters: "it reached 71 degrees" means nothing without "it idles at
        /// 44", and a radio that dies seconds after unkeying leaves its cooling
        /// curve here and nowhere else. Not one second because this line is
        /// unconditional — every operator, every session, for the life of the
        /// app — and a second-by-second temperature nobody asked for is how
        /// <see cref="MeterTraceStream"/>'s 139-lines-a-second incident
        /// started. Thirty seconds is 120 lines an hour while resting, and the
        /// window's own min/max/n still shows a drift.</para>
        /// </summary>
        public const int RestingWindowMs = 30000;

        /// <summary>The window in force for a given transmit state.</summary>
        public static int WindowFor(bool transmittingOrTuning) =>
            transmittingOrTuning ? TransmitWindowMs : RestingWindowMs;

        /// <summary>
        /// A window this old is abandoned rather than reported: its samples and
        /// the one that finally closed it are describing different situations.
        /// Four times the resting window, so an ordinary resting window never
        /// trips it and a disconnect, a reconnect or a sleeping machine always
        /// does.
        /// </summary>
        public const int StaleWindowMs = 4 * RestingWindowMs;

        /// <summary>
        /// A radio that publishes no supply-voltage meter is not a radio reading
        /// zero volts. A FLEX-6300 — Don's radio, and the whole reason this line
        /// exists — publishes FWDPWR, REFPWR, SWR, PATEMP, HWALC and no volts at
        /// all (#566, verified against his own meter inventory), so
        /// <c>VoltsDataReady</c> never fires and the field keeps its initial
        /// zero. Say "none" rather than "0.0", because 0.0 volts on a
        /// transmitting radio would be a finding and "we never got one" is not.
        /// </summary>
        public static bool RadioPublishesSupplyVoltage(float lastVoltsReading) =>
            lastVoltsReading > 0f;

        /// <summary>
        /// The <c>captureMeters:</c> line for one closed window. Separate from
        /// the emitting so a test can read the sentence rather than the source.
        /// </summary>
        /// <param name="min">Lowest PA temperature in the window.</param>
        /// <param name="max">Highest.</param>
        /// <param name="last">The most recent reading.</param>
        /// <param name="count">How many samples the window held.</param>
        /// <param name="volts">The supply voltage as last reported, or a value
        /// at or below zero when the radio publishes no such meter.</param>
        /// <param name="transmitting">Whether the window closed while
        /// transmitting or tuning — on the line, because a temperature read
        /// while resting and one read under load are different measurements and
        /// a reader who cannot tell them apart will average them.</param>
        public static string Format(float min, float max, float last, int count,
                                    float volts, bool transmitting)
        {
            string v = RadioPublishesSupplyVoltage(volts)
                ? volts.ToString("0.##", CultureInfo.InvariantCulture)
                : "none";
            return CaptureMetersLine
                + " state=" + (transmitting ? "tx" : "rest")
                + " paTemp min=" + min.ToString("0.##", CultureInfo.InvariantCulture)
                + " max=" + max.ToString("0.##", CultureInfo.InvariantCulture)
                + " last=" + last.ToString("0.##", CultureInfo.InvariantCulture)
                + " n=" + count.ToString(CultureInfo.InvariantCulture)
                + " degC volts=" + v;
        }

        // ── The coalescer ────────────────────────────────────────────────
        //
        // Same discipline as MeterTraceStream: accumulate at meter rate, emit
        // once a window with min, max, last and the sample count, so the peaks
        // survive without the volume. Per rig instance, because two radios are
        // two amplifiers.

        private readonly object _gate = new object();
        private float _min, _max, _last;
        private int _count;
        private int _windowStart;
        private bool _windowTransmitting;

        /// <summary>
        /// Feed one PA temperature reading. Returns the line to trace when the
        /// window closed on this sample, or null.
        ///
        /// <para>The supply voltage rides along as a SNAPSHOT — the last value
        /// the radio reported, read at the moment the window closes — rather
        /// than as a windowed min/max, because nothing feeds it into this
        /// coalescer. Making it windowed means one line inside
        /// <c>VoltsDataHandler</c>, which Sprint 45 Track H was told not to
        /// touch; #566's subsystem gives every selected meter the same
        /// treatment and ends the asymmetry.</para>
        /// </summary>
        /// <param name="celsius">The reading, as FlexLib delivered it.</param>
        /// <param name="volts">The last supply-voltage reading, or zero when
        /// the radio publishes no such meter.</param>
        /// <param name="transmittingOrTuning">Transmit state now.</param>
        /// <param name="nowTick"><c>Environment.TickCount</c>, passed in so the
        /// clock can be driven by a test.</param>
        public string Report(float celsius, float volts, bool transmittingOrTuning, int nowTick)
        {
            lock (_gate)
            {
                // A gap longer than this means the window's oldest sample is
                // describing a different situation — the radio went away and
                // came back, or the machine slept. Reporting min and max across
                // that is a false statistic, and n is then a count of nothing.
                //
                // Handled HERE rather than by a Reset() the disconnect path
                // calls, because a hook with one caller is a hook with no
                // caller the day somebody edits that path — and this catches
                // every cause of a gap, not just the one anybody thought of.
                if (_count > 0 && (nowTick - _windowStart) > StaleWindowMs)
                {
                    _count = 0;
                }

                if (_count == 0)
                {
                    _windowStart = nowTick;
                    _windowTransmitting = transmittingOrTuning;
                    _min = _max = _last = celsius;
                    _count = 1;
                }
                else
                {
                    if (celsius < _min) _min = celsius;
                    if (celsius > _max) _max = celsius;
                    _last = celsius;
                    _count++;
                    // Keying up mid-window makes it a transmit window. The
                    // alternative — keeping the state it opened with — labels
                    // the first second of every transmission "rest", which is
                    // the second a thermal question cares most about.
                    if (transmittingOrTuning) _windowTransmitting = true;
                }

                // The window a sample belongs to is decided by the state of the
                // window, not by the state at this instant: a transmission that
                // starts 900 ms into a resting window must not wait the rest of
                // the 30 seconds to say so.
                if ((nowTick - _windowStart) < WindowFor(_windowTransmitting)) return null;

                string line = Format(_min, _max, _last, _count, volts, _windowTransmitting);
                _count = 0;
                return line;
            }
        }

    }
}
