using System;
using System.Collections.Generic;
using System.Globalization;
using JJTrace;

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
    /// <para><b>#566 REPLACED THE LIST THAT USED TO LIVE HERE (folded
    /// 2026-10-01, Track IJK).</b> Ruled by Noel 2026-09-22: ONE operator-chosen
    /// meter selection feeds the alarms and the capture, so an alarm on a meter
    /// means that meter is recorded, a meter can be recorded without an alarm
    /// on it, and there is no hardcoded list in either place. Until the fold
    /// this class carried a four-entry list — forward power, reflected power,
    /// PA temperature, <c>+13.8A</c> — beside the alarm subsystem's own
    /// recorded set, which is two vocabularies for one thing. The list is
    /// gone. What the <c>captureMeters:</c> line records is now the alarm
    /// service's recorded set (<c>Radios.Alarms.AlarmService.RecordedMetersResolved</c>:
    /// every ENABLED alarm's meter plus the operator's record-only choices,
    /// seeded for a radio with no alarm file yet with its PA temperature and
    /// supply meters, which is the measurement that put them here), pushed to
    /// the rig by <c>JJFlexWpf.OperatorAlarmHost</c> through
    /// <c>FlexBase.SetCaptureSelection</c>. Temperature is written only while
    /// PATEMP is selected; the voltage field follows the selected supply meter
    /// and says <c>not-selected</c> when none is. With a supply meter selected
    /// and temperature not, the supply meter's own cadence drives the window
    /// and the line says <c>paTemp not-selected</c>
    /// (<see cref="ReportWithoutTemperature"/>; Astra's Track IJK review,
    /// blocker 4).</para>
    ///
    /// <para><b>Forward and reflected power are NOT a selection, and that is
    /// deliberate.</b> They ride the <c>txMeters:</c> line, which is the
    /// transmit-safety evidence record the reflected-power cut and
    /// <c>TxFactAudit</c> read (#625). An operator's recording preference must
    /// not be able to switch off the evidence that a cut was right, so that
    /// line is owned by the kill switch's own path and stays outside the
    /// recorded set. DRAFT for Noel: the ruling says "no hardcoded list in
    /// either place", and this is the one place a fixed pair remains, for a
    /// stated reason.</para>
    /// </summary>
    public sealed class CaptureMeterSet
    {

        /// <summary>The <c>txMeters:</c> line, written by
        /// <c>FlexBase.traceTxMeters</c> while transmitting or tuning.</summary>
        public const string TxMetersLine = "txMeters:";

        /// <summary>The <c>captureMeters:</c> line, written by this class.</summary>
        public const string CaptureMetersLine = "captureMeters:";

        /// <summary>
        /// What a <c>txMeters:</c> line is, in the words of the code that
        /// writes it — handed to the sink with every such line so the file
        /// introduces the kind where it first appears (#625). Owned here rather
        /// than beside <c>FlexBase.traceTxMeters</c> because this class is
        /// already the one place the recorded set is written down, and the
        /// fields named below are the ones that method formats: change either
        /// only in step with the other. DRAFT for Noel.
        /// </summary>
        /// <remarks>
        /// <para><b>H17 (Sol's H16 review) changed two claims here.</b> It said
        /// "about once a second"; the writer is rate-limited to at most once a
        /// second while transmitting and at most once every 250 ms while
        /// tuning, and writes only when a meter sample arrives to drive it. And
        /// it called every field a reading while the writer printed
        /// placeholders — forward power's -150 dBm initialiser, reflected
        /// power's default zero — before the radio had reported anything. The
        /// writer now prints <see cref="NoSample"/> for any value the radio has
        /// not reported since the connection was made, and this says what that
        /// token means.</para>
        /// <para><b>H18 (Sol's H17 review) named the window, field by
        /// field.</b> "Since this connection was made" was the wrong window for
        /// a transmit snapshot: the peak in brackets already reset at key-down
        /// while SC_MIC kept the last transmission's value, so one line carried
        /// two windows and a second transmission could print the first one's
        /// mic level. Every value now comes from the current transmission or
        /// tune, and this says so, and says when that starts. The peak is the
        /// writer's own, for that window and the copy "via" names. It also
        /// stopped saying the copy-count line "says 'census'", which it never
        /// did.</para>
        /// </remarks>
        public static readonly TraceRecordKind TxMetersRecord = new TraceRecordKind(
            "txMeters",
            "lines that begin 'txMeters: state=' are JJ Flexible's transmit meter snapshots. One is written"
            + " when a meter reading arrives while the radio is transmitting or tuning, at most once a second"
            + " while transmitting and at most four times a second while tuning. state says whether the radio"
            + " was transmitting, tuning, or both. Every value on the line comes from the current transmission"
            + " or tune, which starts when the radio reports that it is transmitting or when a tune begins, and"
            + " starts again on a new connection; nothing the radio sent before that is used. SC_MIC is the"
            + " latest mic level in dB, and 'via' names the copy of that meter JJ Flexible believes. The figure"
            + " in brackets is the highest SC_MIC reading from that copy so far in this transmission or tune; if"
            + " the believed copy changes, it starts again. SWALC is the latest software ALC reading. fwd and"
            + " refl are the latest forward and reflected power in dBm, fwdW and reflW the same in watts, back"
            + " the share of power coming back, SWRraw the latest SWR the radio reports and SWRcalc the SWR"
            + " computed from the two powers. A value written as '" + NoSample + "' is one the radio has not"
            + " reported during this transmission or tune, so there is no reading to give; a value worked out"
            + " from it is '" + NoSample + "' too. 'n/a' means both powers had arrived but the value could not"
            + " honestly be worked out from them, for example because there was too little forward power. A"
            + " 'txMeters:' line that instead counts the copies of a meter, or names a copy as elected or"
            + " re-elected, records which copy of a duplicated meter is being read; it is not a reading.");

        /// <summary>
        /// What a <c>txMeters:</c> field says in place of a number when the
        /// radio has not reported that value during the current transmission
        /// or tune (#625, H17; the window since H18). The same word the <c>captureMeters:</c> line already
        /// uses for supply voltage (<c>volts=no-sample</c>), so a reader meets
        /// one vocabulary, not two. Parsers read it:
        /// <c>tools/TxFactAudit/TraceMeters.cs</c>.
        /// </summary>
        public const string NoSample = "no-sample";

        /// <summary>
        /// What a <c>captureMeters:</c> line is, in the words of the code that
        /// writes it — <see cref="Format"/> below renders every field named
        /// here. DRAFT for Noel.
        /// </summary>
        public static readonly TraceRecordKind CaptureMetersRecord = new TraceRecordKind(
            "captureMeters",
            "lines that begin 'captureMeters:' are the amplifier temperature and the supply voltage as the radio"
            + " reported them. paTemp gives the lowest, highest and last temperature in degrees C over the window"
            + " and n the number of samples; 'paTemp none n=0' means the radio sent no temperature in that window,"
            + " and 'paTemp not-selected' means the operator took PA temperature out of the recorded meters, so"
            + " none was asked for. volts is the supply voltage at the moment the line was written, or the reason there is none, and"
            + " voltsAge says how old that reading was when it is older than the window. state says whether the"
            + " window was taken while transmitting or tuning (tx) or at rest. One line a second while"
            + " transmitting or tuning, one every thirty seconds otherwise. A line that ends"
            + " 'partial=connection_dropped' is a window cut short because the connection to the radio was lost.");

        /// <summary>
        /// The radio's own name for its PA temperature meter — the one meter
        /// this class's window is about. Used by the rig to ask the selection
        /// whether temperature is recorded at all; it names nothing else.
        /// </summary>
        public const string PaTemperatureMeterName = "PATEMP";

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
        /// The supply meter the voltage field prefers when the operator has
        /// selected more than one: <c>+13.8A</c>, the one FlexLib subscribes
        /// <c>Volts_DataReady</c> to and the one every capture before the fold
        /// carried, so the field keeps meaning the same thing across the
        /// change. Any other selected volts meter is used when this one is not
        /// selected or not published.
        ///
        /// <para><b>A FLEX-6300 DOES publish it.</b> The constant this replaced
        /// stood in for a helper that decided presence by asking whether the
        /// last reading was above zero, under a comment saying a 6300 "publishes
        /// no volts at all (#566, verified against its meter inventory)". #566's
        /// 2026-09-22 correction, taken from a 6300's own capture, records
        /// <c>+13.8A</c> before the fuse and <c>+13.8B</c> after it, and strikes
        /// that sentence through — it had claimed to be verified against an
        /// inventory that said the opposite. <b>So a "no meter" reading in a
        /// 6300 capture is a FINDING to chase, not the model behaving
        /// normally</b> (#597), and voltage sag under load is one of the three
        /// standing explanations for a 6300 shutting itself off.</para>
        /// </summary>
        public const string PreferredSupplyVoltageMeterName = "+13.8A";

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
                case SupplyVoltageState.NotSelected:
                    // The operator took supply voltage out of the recorded set
                    // (#566). Not "no meter" — the radio may well publish one —
                    // and not "no sample": a choice, written as one.
                    return "volts=not-selected";
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
        /// <param name="temperatureSelected">Whether PA temperature is in the
        /// operator's recorded set (#566). False renders <c>paTemp not-selected</c>:
        /// a window driven by the supply meter alone must not claim the radio
        /// sent no temperature when nobody asked it for one — the same
        /// distinction <see cref="FormatVolts"/> draws between
        /// <c>not-selected</c> and <c>no-meter</c>.</param>
        public static string Format(float min, float max, float last, int count,
                                    SupplyVoltage volts, bool transmitting,
                                    string partialReason = null, bool temperatureSelected = true)
        {
            string paTemp = count > 0
                ? "paTemp min=" + min.ToString("0.##", CultureInfo.InvariantCulture)
                  + " max=" + max.ToString("0.##", CultureInfo.InvariantCulture)
                  + " last=" + last.ToString("0.##", CultureInfo.InvariantCulture)
                  + " n=" + count.ToString(CultureInfo.InvariantCulture)
                : temperatureSelected ? "paTemp none n=0" : "paTemp not-selected";

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
        /// Whether a window is open at all — separate from <see cref="_count"/>
        /// since Astra's Track IJK review (blocker 4), because a window can now
        /// be open with no temperature in it: one driven by the supply meter
        /// while PA temperature is not selected. Before that the two were one
        /// fact, and "no samples" meant "no window".
        /// </summary>
        private bool _open;

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
                OpenOrContinueLocked(transmittingOrTuning, nowTick);

                if (_count == 0)
                {
                    _min = _max = _last = celsius;
                    _count = 1;
                }
                else
                {
                    if (celsius < _min) _min = celsius;
                    if (celsius > _max) _max = celsius;
                    _last = celsius;
                    _count++;
                }

                // The window a sample belongs to is decided by the state of the
                // window, not by the state at this instant: a transmission that
                // starts 900 ms into a resting window must not wait the rest of
                // the 30 seconds to say so.
                if ((nowTick - _windowStart) < WindowFor(_windowTransmitting)) return null;

                string line = Format(_min, _max, _last, _count, volts, _windowTransmitting);
                CloseLocked();
                return line;
            }
        }

        /// <summary>
        /// Feed one supply-voltage reading's ARRIVAL, for a radio whose
        /// operator records supply voltage but not PA temperature. Returns the
        /// line to trace when the window closed on this arrival, or null.
        ///
        /// <para><b>Why this exists (Astra's Track IJK review, blocker 4,
        /// introduced by the one-selection fold).</b> The window was driven
        /// only by PA temperature samples, and the fold made the rig return
        /// before feeding one unless PATEMP was selected — so an operator who
        /// kept a supply meter ticked and unticked temperature lost every
        /// periodic <c>captureMeters:</c> line, voltage history included, while
        /// the dialog said supply voltage was recorded. The supply meter's own
        /// cadence now drives the window in that case. The reading's value is
        /// not accumulated here: the voltage field was always a snapshot read
        /// when the window closes (see <see cref="Report"/>), and this keeps
        /// that one meaning rather than adding a second.</para>
        /// </summary>
        public string ReportWithoutTemperature(SupplyVoltage volts, bool transmittingOrTuning, int nowTick)
        {
            lock (_gate)
            {
                OpenOrContinueLocked(transmittingOrTuning, nowTick);
                if ((nowTick - _windowStart) < WindowFor(_windowTransmitting)) return null;

                string line = Format(0f, 0f, 0f, _count, volts, _windowTransmitting, temperatureSelected: false);
                CloseLocked();
                return line;
            }
        }

        /// <summary>
        /// Open a window at <paramref name="nowTick"/> if none is open, or
        /// continue the open one — abandoning it first if it has gone stale.
        /// Under the gate.
        /// </summary>
        private void OpenOrContinueLocked(bool transmittingOrTuning, int nowTick)
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
            if (_open && (nowTick - _windowStart) > StaleWindowMs) CloseLocked();

            if (!_open)
            {
                _open = true;
                _windowStart = nowTick;
                _windowTransmitting = transmittingOrTuning;
                _count = 0;
            }
            else if (transmittingOrTuning)
            {
                // Keying up mid-window makes it a transmit window. The
                // alternative — keeping the state it opened with — labels
                // the first second of every transmission "rest", which is
                // the second a thermal question cares most about.
                _windowTransmitting = true;
            }
        }

        private void CloseLocked()
        {
            _open = false;
            _count = 0;
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
        /// <param name="temperatureSelected">Whether PA temperature is in the
        /// recorded set at this moment; false renders <c>paTemp not-selected</c>
        /// rather than claiming the radio sent none (#566, blocker 4).</param>
        public string Flush(SupplyVoltage volts, bool transmittingOrTuning, string reason, int nowTick,
                            bool temperatureSelected = true)
        {
            lock (_gate)
            {
                bool stale = _open && (nowTick - _windowStart) > StaleWindowMs;
                if (!_open || stale)
                {
                    CloseLocked();
                    return Format(0f, 0f, 0f, 0, volts, transmittingOrTuning, reason, temperatureSelected);
                }

                string line = Format(_min, _max, _last, _count, volts, _windowTransmitting, reason, temperatureSelected);
                CloseLocked();
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
        /// <summary>The radio published a meter list and the selected supply
        /// meter is not in it.</summary>
        NoMeter,
        /// <summary>The operator's recorded set (#566) holds no supply-voltage
        /// meter, so none is read. A choice, not an absence.</summary>
        NotSelected,
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

        /// <summary>The recorded set holds no supply-voltage meter.</summary>
        public static SupplyVoltage NotSelected() =>
            new SupplyVoltage(SupplyVoltageState.NotSelected, 0f, null);

        /// <summary>The meter exists and has never reported.</summary>
        public static SupplyVoltage NoSample() =>
            new SupplyVoltage(SupplyVoltageState.NoSample, 0f, null);

        /// <summary>A reading, with the age of the sample where it is known.
        /// Zero is a reading like any other.</summary>
        public static SupplyVoltage Reading(float volts, TimeSpan? age) =>
            new SupplyVoltage(SupplyVoltageState.Reading, volts, age);
    }
}
