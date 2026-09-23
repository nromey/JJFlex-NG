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
        /// The radio's own name for the supply-voltage meter we record — the one
        /// FlexLib subscribes <c>Volts_DataReady</c> to.
        ///
        /// <para><b>A FLEX-6300 DOES publish it.</b> This constant replaces a
        /// helper that decided presence by asking whether the last reading was
        /// above zero, under a comment saying Don's 6300 "publishes no volts at
        /// all (#566, verified against his own meter inventory)". #566's
        /// 2026-09-22 correction, taken from Don's own capture, records
        /// <c>+13.8A</c> before the fuse and <c>+13.8B</c> after it, and strikes
        /// that sentence through — it had claimed to be verified against an
        /// inventory that said the opposite. <b>So a "no meter" reading in one
        /// of Don's captures is a FINDING to chase, not the model behaving
        /// normally</b> (#597), and voltage sag under load is one of the three
        /// standing explanations for a 6300 shutting itself off.</para>
        ///
        /// <para>Only <c>+13.8A</c> is recorded here. <c>+13.8B</c> is a second
        /// meter with its own story to tell — after the fuse rather than before
        /// — and choosing between them is #566's selection job, not a silent
        /// fallback.</para>
        /// </summary>
        public const string SupplyVoltageMeterName = "+13.8A";

        /// <summary>
        /// Why a window was closed early. Appears on the line as
        /// <c>partial=connection_dropped</c> so a reader can tell a window that
        /// ran its full course from one that was cut short.
        /// </summary>
        public const string PartialConnectionDropped = "connection_dropped";

        /// <summary>
        /// Render the supply-voltage field.
        ///
        /// <para><b>Four states, four renderings (#597).</b> The first build
        /// mapped every value at or below zero to <c>none</c>, which made "this
        /// radio has no such meter", "nothing has arrived yet", "this reading is
        /// minutes old" and "it really does read zero" all look identical — and
        /// a genuine zero on a transmitting radio is the single most interesting
        /// thing this field could ever say.</para>
        /// </summary>
        /// <param name="volts">What the meter inventory knows.</param>
        /// <param name="windowMs">The window this line covers. A reading older
        /// than its own window did not come from the period being reported, so
        /// its age is put on the line; a reading younger than the window needs
        /// no qualification and gets none.</param>
        public static string FormatVolts(SupplyVoltage volts, int windowMs)
        {
            switch (volts.State)
            {
                case SupplyVoltageState.InventoryUnknown:
                    // Not the same claim as "no meter". The radio has not told
                    // us its meter list yet, so we do not know either way, and
                    // an absence of evidence must not be written down as
                    // evidence of absence.
                    return "volts=unknown";
                case SupplyVoltageState.NoMeter:
                    return "volts=no-meter";
                case SupplyVoltageState.NoSample:
                    return "volts=no-sample";
                default:
                    string v = "volts=" + volts.Volts.ToString("0.##", CultureInfo.InvariantCulture);
                    TimeSpan? age = volts.Age;
                    if (age != null && age.Value.TotalMilliseconds > windowMs)
                    {
                        v += " voltsAge="
                            + age.Value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";
                    }
                    return v;
            }
        }

        /// <summary>
        /// The <c>captureMeters:</c> line for one window. Separate from the
        /// emitting so a test can read the sentence rather than the source.
        /// </summary>
        /// <param name="min">Lowest PA temperature in the window.</param>
        /// <param name="max">Highest.</param>
        /// <param name="last">The most recent reading.</param>
        /// <param name="count">How many samples the window held. Zero is
        /// legitimate on a flush and renders <c>paTemp none n=0</c>: "the radio
        /// sent no temperature in this window" is itself the answer to #494's
        /// question and must not be indistinguishable from "we wrote nothing
        /// down".</param>
        /// <param name="volts">The supply voltage as the meter inventory knows
        /// it.</param>
        /// <param name="transmitting">Whether the window closed while
        /// transmitting or tuning — on the line, because a temperature read
        /// while resting and one read under load are different measurements and
        /// a reader who cannot tell them apart will average them.</param>
        /// <param name="partialReason">Non-null when the window was cut short
        /// rather than closing on its own.</param>
        public static string Format(float min, float max, float last, int count,
                                    SupplyVoltage volts, bool transmitting,
                                    string partialReason = null)
        {
            string paTemp = count > 0
                ? "paTemp min=" + min.ToString("0.##", CultureInfo.InvariantCulture)
                  + " max=" + max.ToString("0.##", CultureInfo.InvariantCulture)
                  + " last=" + last.ToString("0.##", CultureInfo.InvariantCulture)
                  + " n=" + count.ToString(CultureInfo.InvariantCulture)
                : "paTemp none n=0";

            string line = CaptureMetersLine
                + " state=" + (transmitting ? "tx" : "rest")
                + " " + paTemp
                + " degC " + FormatVolts(volts, WindowFor(transmitting));
            if (!string.IsNullOrEmpty(partialReason)) line += " partial=" + partialReason;
            return line;
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
        /// <para>The supply voltage rides along as a SNAPSHOT — the meter's own
        /// last value and the age of it, read at the moment the window closes —
        /// rather than as a windowed min/max, because nothing feeds it into this
        /// coalescer. Making it windowed means one line inside
        /// <c>VoltsDataHandler</c>, which Sprint 45 Track H was told not to
        /// touch; #566's subsystem gives every selected meter the same
        /// treatment and ends the asymmetry. The age is what keeps a snapshot
        /// honest: a held value and a fresh one look identical without it.</para>
        /// </summary>
        /// <param name="celsius">The reading, as FlexLib delivered it.</param>
        /// <param name="volts">What the meter inventory knows about supply
        /// voltage at this moment.</param>
        /// <param name="transmittingOrTuning">Transmit state now.</param>
        /// <param name="nowTick"><c>Environment.TickCount</c>, passed in so the
        /// clock can be driven by a test.</param>
        public string Report(float celsius, SupplyVoltage volts, bool transmittingOrTuning, int nowTick)
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

        /// <summary>
        /// Close whatever window is open RIGHT NOW and hand back its line,
        /// marked with why it was cut short. Never returns null: a window with
        /// no samples in it still produces <c>paTemp none n=0</c>.
        ///
        /// <para><b>Why this exists, and why it emits even when empty (#598).</b>
        /// A line was written only when a window closed on its own — one a
        /// second while transmitting, one every thirty seconds at rest — and
        /// nothing closed the open one before the session was archived. So a
        /// connection lost inside the first second saved NO temperature at all,
        /// and every later drop lost the final partial window, which is the last
        /// thing the radio said before it died. The moment of death was
        /// precisely the part that went missing. An empty flush still says
        /// something a reader needs: that the radio produced no temperature in
        /// this window, which is #494's question, and is not the same as the
        /// application having failed to write one down.</para>
        ///
        /// <para><b>A stale window is reported as empty, not as a statistic.</b>
        /// Same rule as <see cref="Report"/>: samples either side of a very long
        /// gap describe different situations, and min/max across them is a
        /// fiction with a plausible-looking <c>n</c> on it.</para>
        ///
        /// <para>The caller writes the line. This is deliberately a pure
        /// function of the window so a test can read the sentence, and so the
        /// one caller that traces it is the one place a trace level is
        /// chosen.</para>
        /// </summary>
        /// <param name="volts">What the meter inventory knows about supply
        /// voltage at this moment.</param>
        /// <param name="transmittingOrTuning">Transmit state now. Used only when
        /// no window is open — an open window keeps the state it earned.</param>
        /// <param name="reason">Why the window is being cut short, e.g.
        /// <see cref="PartialConnectionDropped"/>.</param>
        /// <param name="nowTick"><c>Environment.TickCount</c>.</param>
        public string Flush(SupplyVoltage volts, bool transmittingOrTuning, string reason, int nowTick)
        {
            lock (_gate)
            {
                bool stale = _count > 0 && (nowTick - _windowStart) > StaleWindowMs;
                if (_count == 0 || stale)
                {
                    _count = 0;
                    return Format(0f, 0f, 0f, 0, volts, transmittingOrTuning, reason);
                }

                string line = Format(_min, _max, _last, _count, volts, _windowTransmitting, reason);
                _count = 0;
                return line;
            }
        }
    }

    /// <summary>
    /// What is known about the supply-voltage meter at the moment a
    /// <c>captureMeters:</c> line is written.
    /// </summary>
    public enum SupplyVoltageState
    {
        /// <summary>The radio has not told us its meter list yet, so whether it
        /// publishes supply voltage is simply unknown. Distinct from
        /// <see cref="NoMeter"/> on purpose: an absence of evidence is not
        /// evidence of absence, and writing it down as one is how the claim that
        /// a 6300 publishes no voltage meter survived long enough to reach
        /// shipped code.</summary>
        InventoryUnknown,
        /// <summary>The radio published a meter list and
        /// <see cref="CaptureMeterSet.SupplyVoltageMeterName"/> is not in
        /// it.</summary>
        NoMeter,
        /// <summary>The meter exists and has never reported a value.</summary>
        NoSample,
        /// <summary>A real reading, zero included.</summary>
        Reading,
    }

    /// <summary>
    /// A supply-voltage reading with its provenance attached — present or
    /// absent, sampled or not, and how old.
    ///
    /// <para><b>Presence comes from the meter inventory, never from the value
    /// (#597).</b> Asking "is the last reading above zero?" answers a different
    /// question than "does this radio have the meter?", and the two were
    /// conflated into a single word, <c>none</c>, covering four different
    /// states. <see cref="MeterInventory"/> already resolves identity, reports
    /// <see cref="MeterReading.HasReading"/> and stamps every sample with the
    /// time it arrived, so this joins that seam rather than inventing
    /// one.</para>
    /// </summary>
    public readonly struct SupplyVoltage
    {
        private SupplyVoltage(SupplyVoltageState state, float volts, TimeSpan? age)
        {
            State = state;
            Volts = volts;
            Age = age;
        }

        /// <summary>Which of the four states this is.</summary>
        public SupplyVoltageState State { get; }

        /// <summary>The reading, meaningful only when
        /// <see cref="State"/> is <see cref="SupplyVoltageState.Reading"/>.</summary>
        public float Volts { get; }

        /// <summary>How long ago the reading arrived, where that is known.</summary>
        public TimeSpan? Age { get; }

        /// <summary>The radio has not published a meter list yet.</summary>
        public static SupplyVoltage Unknown() =>
            new SupplyVoltage(SupplyVoltageState.InventoryUnknown, 0f, null);

        /// <summary>The radio published its meters and this one is not among
        /// them.</summary>
        public static SupplyVoltage NoMeter() =>
            new SupplyVoltage(SupplyVoltageState.NoMeter, 0f, null);

        /// <summary>The meter exists and has never reported.</summary>
        public static SupplyVoltage NoSample() =>
            new SupplyVoltage(SupplyVoltageState.NoSample, 0f, null);

        /// <summary>A reading, with the age of the sample where it is known.
        /// Zero is a reading like any other.</summary>
        public static SupplyVoltage Reading(float volts, TimeSpan? age) =>
            new SupplyVoltage(SupplyVoltageState.Reading, volts, age);
    }
}
