#nullable enable
using System;
using System.Collections.Generic;

namespace Radios.Alarms
{
    /// <summary>Which side of the line is the alarm side. Both boundaries are INCLUSIVE.</summary>
    public enum AlarmDirection
    {
        /// <summary>Value at or above the threshold. Clears at or below threshold minus hysteresis.</summary>
        AtOrAbove,

        /// <summary>Value at or below the threshold. Clears at or above threshold plus hysteresis.</summary>
        AtOrBelow,
    }

    /// <summary>What the alarm measures.</summary>
    public enum AlarmCondition
    {
        /// <summary>The meter's level against a fixed threshold.</summary>
        Level,

        /// <summary>The rise from a baseline the operator captured deliberately, in receive.</summary>
        RiseFromBaseline,

        /// <summary>A smoothed rise over about ninety seconds — the optional, disabled-by-default trend.
        /// Not fan-fault detection; a configurable hypothesis (design section 1).</summary>
        RisingFast,
    }

    /// <summary>How many fresh samples on the alarm side it takes to fire.</summary>
    public enum AlarmPersistence
    {
        /// <summary>One valid, newly delivered observation. The default: waiting for a second
        /// slow temperature update would hide a real warning.</summary>
        FirstFreshSample,

        /// <summary>At least N distinct fresh observations spanning at least D seconds, all on the alarm side.</summary>
        Sustained,
    }

    /// <summary>When the condition is judged at all.</summary>
    public enum AlarmScope
    {
        /// <summary>Whenever connected, receive and transmit. Cooling between transmissions matters.</summary>
        WheneverConnected,

        /// <summary>Only while this client is transmitting, for meters whose receive values mean nothing.
        /// Its inactive state is "waiting for transmit", not "healthy".</summary>
        TransmitOnly,
    }

    /// <summary>What the operator is asked to do when it fires. Chooses the sentence, never a radio command.</summary>
    public enum AlarmActionClass
    {
        /// <summary>Release transmit now (in transmit) or stay in receive (in receive). The presets' choice.</summary>
        StopTransmit,

        /// <summary>Say what crossed and leave the action to the operator. A signal-strength watch.</summary>
        NotifyOnly,
    }

    /// <summary>One thing wrong with a definition, by field, with the lexicon key that explains it.</summary>
    public sealed record AlarmValidationProblem(string Field, string LexiconKey);

    /// <summary>
    /// An operator alarm, as data. Immutable; edit with <c>with</c> and bump
    /// <see cref="Revision"/> through <see cref="NextRevision"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the engine needs and nothing it computes. Values are in the
    /// meter's own units, always — no implicit conversion from dBm to watts,
    /// Celsius to anything, or raw SWR to a trustworthy ratio (design
    /// section 2). The meter is named by <see cref="Selector"/>, never by the
    /// radio's numeric index.
    /// </para>
    /// <para>
    /// The defaults below are the GENERAL defaults for an unfamiliar meter.
    /// The presets in <see cref="AlarmPresets"/> override several of them on
    /// purpose and say why.
    /// </para>
    /// </remarks>
    public sealed record AlarmDefinition
    {
        /// <summary>Stable identity across edits. A GUID string.</summary>
        public string Id { get; init; } = "";

        /// <summary>Bumped on every saved edit. Events carry the revision they were judged under.</summary>
        public int Revision { get; init; } = 1;

        /// <summary>The operator's name for it. Plain data, never a template.</summary>
        public string Name { get; init; } = "";

        /// <summary>The operator's intent. Persisted; live data is reacquired after a restart.</summary>
        public bool Enabled { get; init; }

        /// <summary>The radio this alarm belongs to. Values from one radio never apply to another.</summary>
        public string RadioSerial { get; init; } = "";

        /// <summary>Which meter, by name, source, source index and unit.</summary>
        public MeterSelector Selector { get; init; } = new MeterSelector("", "", 0, Flex.Smoothlake.FlexLib.MeterUnits.None, "");

        public AlarmCondition Condition { get; init; } = AlarmCondition.Level;
        public AlarmDirection Direction { get; init; } = AlarmDirection.AtOrAbove;

        /// <summary>Level: the line. RiseFromBaseline: the rise. RisingFast: the rise over the window.</summary>
        public double Threshold { get; init; }

        /// <summary>Non-negative, in the meter's units. Zero means no band.</summary>
        public double Hysteresis { get; init; }

        public AlarmPersistence Persistence { get; init; } = AlarmPersistence.FirstFreshSample;
        public int SustainedCount { get; init; } = 2;
        public double SustainedSeconds { get; init; } = 1;

        /// <summary>Longest silence before the data is reported unavailable. Provisional 5 s for slow meters.</summary>
        public double FreshnessAllowanceSeconds { get; init; } = 5;

        public AlarmScope Scope { get; init; } = AlarmScope.WheneverConnected;

        /// <summary>How often the current warning repeats while active and fresh. Zero disables reminders.</summary>
        public double ReminderIntervalSeconds { get; init; } = 30;

        public AlarmActionClass Action { get; init; } = AlarmActionClass.StopTransmit;

        /// <summary>A further move of this much in the bad direction is announced at once, bypassing
        /// acknowledgement and snooze — once per step. Zero disables.</summary>
        public double WorseningStep { get; init; }

        /// <summary>Fresh samples beyond the clear boundary needed to clear, spanning <see cref="ClearSeconds"/>.</summary>
        public int ClearSamples { get; init; } = 2;
        public double ClearSeconds { get; init; } = 1;

        /// <summary>A meter-specific documented sentinel that means "no reading", or null. Never generalised.</summary>
        public double? SentinelValue { get; init; }

        /// <summary>The shipped preset this came from, or empty for an operator's own definition.</summary>
        public string PresetKey { get; init; } = "";

        /// <summary>True for a temporary definition the operator marked as a test (bench step 5).</summary>
        public bool IsTest { get; init; }

        // ── RisingFast parameters ──

        /// <summary>Length of the observation window. First available after this much uninterrupted data.</summary>
        public double TrendWindowSeconds { get; init; } = 100;

        /// <summary>Width of each endpoint band whose median is compared.</summary>
        public double TrendBandSeconds { get; init; } = 10;

        /// <summary>Distinct observations each endpoint band must hold.</summary>
        public int TrendMinSamplesPerBand { get; init; } = 3;

        /// <summary>The two median observation times must be this far apart, inclusive.</summary>
        public double TrendIntervalMinSeconds { get; init; } = 85;
        public double TrendIntervalMaxSeconds { get; init; } = 95;

        /// <summary>Rise at or below this, over a fully covered window, resets the trend episode.</summary>
        public double TrendResetRise { get; init; } = 10;

        /// <summary>The same definition, one revision on.</summary>
        public AlarmDefinition NextRevision() => this with { Revision = Revision + 1 };

        /// <summary>The clear boundary for a level alarm, in the meter's units.</summary>
        public double ClearBoundary => Direction == AlarmDirection.AtOrAbove
            ? Threshold - Hysteresis
            : Threshold + Hysteresis;

        /// <summary>
        /// True when <paramref name="value"/> is on the alarm side of the
        /// inclusive boundary.
        /// </summary>
        /// <remarks>
        /// <b>Compared at the meter's own precision.</b> Meter values arrive as
        /// single-precision floats and the line is a double, and 12.0 plus a
        /// 0.2 margin is 12.199999999999999 in double while a reading of
        /// exactly 12.2 is 12.19999980926514 as a float — so "equality at the
        /// reset line still clears", which the design promises and the tests
        /// pin, was false by one ulp for a supply alarm until Track IJK's
        /// falling-voltage tests met it. Both sides are taken to float before
        /// the compare, which is the precision the question is asked in.
        /// </remarks>
        public bool IsOnAlarmSide(double value) => Direction == AlarmDirection.AtOrAbove
            ? (float)value >= (float)Threshold
            : (float)value <= (float)Threshold;

        /// <summary>
        /// True when <paramref name="value"/> is at or beyond the clear
        /// boundary — or, with NO margin, strictly past the line.
        /// </summary>
        /// <remarks>
        /// <b>The zero-margin rule, and why it is strict (Astra's Track I
        /// review, finding 12, a defect in the design itself).</b> Both the
        /// trigger and the clear were inclusive, so with a zero margin a value
        /// EQUAL to the line was on the alarm side and beyond the clear at the
        /// same time: a constant reading exactly at 60 fired, cleared two
        /// samples later, and fired again, for ever. A value equal to the line
        /// is on the alarm side by definition and cannot also be clear; with no
        /// band to be inside, clearing needs the value to have left the line.
        /// A positive margin is untouched — equality AT the reset line still
        /// clears, as the PA preset's 58 C always has. DRAFT for Noel: the
        /// review asked him to choose between this rule and requiring a
        /// positive margin; this is the one that changes no ruled number.
        /// </remarks>
        public bool IsBeyondClear(double value) => Direction == AlarmDirection.AtOrAbove
            ? (Hysteresis > 0 ? (float)value <= (float)ClearBoundary : (float)value < (float)Threshold)
            : (Hysteresis > 0 ? (float)value >= (float)ClearBoundary : (float)value > (float)Threshold);

        /// <summary>
        /// The same question for a delta alarm, asked of the measured rise or
        /// fall rather than the level: with a margin, a change at or below
        /// threshold minus margin clears; with none, only a change strictly
        /// below the threshold does. At the meter's precision, as above.
        /// </summary>
        public bool IsChangeBeyondClear(double change) =>
            Hysteresis > 0 ? (float)change <= (float)(Threshold - Hysteresis) : (float)change < (float)Threshold;

        /// <summary>
        /// Positive when <paramref name="later"/> is worse than <paramref name="earlier"/>
        /// in this alarm's direction, by how much.
        /// </summary>
        public double WorseningBetween(double earlier, double later) => Direction == AlarmDirection.AtOrAbove
            ? later - earlier
            : earlier - later;

        // ── factories ──

        public static AlarmDefinition NewLevel(string id, string name, string radioSerial,
            MeterSelector selector, AlarmDirection direction, double threshold, double hysteresis)
            => new AlarmDefinition
            {
                Id = id,
                Name = name,
                RadioSerial = radioSerial,
                Selector = selector,
                Condition = AlarmCondition.Level,
                Direction = direction,
                Threshold = threshold,
                Hysteresis = hysteresis,
            };

        /// <summary>Rise above a captured baseline. Always "above" — a fall is a separate direction alarm on the same idea.</summary>
        public static AlarmDefinition NewRiseFromBaseline(string id, string name, string radioSerial,
            MeterSelector selector, double rise, double resetHysteresis)
            => new AlarmDefinition
            {
                Id = id,
                Name = name,
                RadioSerial = radioSerial,
                Selector = selector,
                Condition = AlarmCondition.RiseFromBaseline,
                Direction = AlarmDirection.AtOrAbove,
                Threshold = rise,
                Hysteresis = resetHysteresis,
            };

        /// <summary>A fall below a captured baseline (the 0.5 V supply drop).</summary>
        public static AlarmDefinition NewFallFromBaseline(string id, string name, string radioSerial,
            MeterSelector selector, double fall, double resetHysteresis)
            => new AlarmDefinition
            {
                Id = id,
                Name = name,
                RadioSerial = radioSerial,
                Selector = selector,
                Condition = AlarmCondition.RiseFromBaseline,
                Direction = AlarmDirection.AtOrBelow,
                Threshold = fall,
                Hysteresis = resetHysteresis,
            };

        /// <summary>The optional trend. DISABLED by default and not labelled fan-fault detection.</summary>
        public static AlarmDefinition NewRisingFast(string id, string name, string radioSerial,
            MeterSelector selector, double rise = 12)
            => new AlarmDefinition
            {
                Id = id,
                Name = name,
                RadioSerial = radioSerial,
                Selector = selector,
                Condition = AlarmCondition.RisingFast,
                Direction = AlarmDirection.AtOrAbove,
                Threshold = rise,
                Hysteresis = 0,
                Enabled = false,
            };

        /// <summary>
        /// Everything wrong with this definition, by field, each with a lexicon
        /// key. Empty means saveable. Mirrors design section 4's list: non-finite
        /// thresholds, negative hysteresis, zero or negative freshness, an invalid
        /// trend interval, and a clear boundary equal to the trigger when nonzero
        /// hysteresis was requested. An unusual threshold outside the published
        /// range is NOT a problem here — it may be intentional, and the editor
        /// shows a review message instead.
        /// </summary>
        public IReadOnlyList<AlarmValidationProblem> Validate()
        {
            var problems = new List<AlarmValidationProblem>();

            if (string.IsNullOrWhiteSpace(Name))
                problems.Add(new AlarmValidationProblem("name", "alarms.validation.name_missing"));
            if (Name != null && Name.Length > MaxNameLength)
                problems.Add(new AlarmValidationProblem("name", "alarms.validation.name_too_long"));
            if (string.IsNullOrWhiteSpace(Selector.Name))
                problems.Add(new AlarmValidationProblem("meter", "alarms.validation.meter_missing"));
            if (!double.IsFinite(Threshold))
                problems.Add(new AlarmValidationProblem("threshold", "alarms.validation.threshold_not_finite"));
            if (!double.IsFinite(Hysteresis) || Hysteresis < 0)
                problems.Add(new AlarmValidationProblem("hysteresis", "alarms.validation.hysteresis_negative"));
            if (!double.IsFinite(FreshnessAllowanceSeconds) || FreshnessAllowanceSeconds <= 0)
                problems.Add(new AlarmValidationProblem("freshness", "alarms.validation.freshness_not_positive"));
            if (Persistence == AlarmPersistence.Sustained && (SustainedCount < 2 || !double.IsFinite(SustainedSeconds) || SustainedSeconds < 0))
                problems.Add(new AlarmValidationProblem("persistence", "alarms.validation.sustained_invalid"));
            if (ClearSamples < 1 || !double.IsFinite(ClearSeconds) || ClearSeconds < 0)
                problems.Add(new AlarmValidationProblem("clear", "alarms.validation.clear_invalid"));
            if (!double.IsFinite(ReminderIntervalSeconds) || ReminderIntervalSeconds < 0)
                problems.Add(new AlarmValidationProblem("reminder", "alarms.validation.reminder_negative"));
            if (!double.IsFinite(WorseningStep) || WorseningStep < 0)
                problems.Add(new AlarmValidationProblem("worsening", "alarms.validation.worsening_negative"));

            if (Condition == AlarmCondition.Level && Hysteresis > 0 && ClearBoundary == Threshold)
                problems.Add(new AlarmValidationProblem("hysteresis", "alarms.validation.clear_equals_trigger"));

            if (Condition == AlarmCondition.RisingFast)
            {
                bool intervalOk = double.IsFinite(TrendIntervalMinSeconds) && double.IsFinite(TrendIntervalMaxSeconds)
                    && TrendIntervalMinSeconds > 0 && TrendIntervalMaxSeconds >= TrendIntervalMinSeconds
                    && TrendWindowSeconds >= TrendIntervalMaxSeconds
                    && TrendBandSeconds > 0 && TrendBandSeconds * 2 <= TrendWindowSeconds
                    && TrendMinSamplesPerBand >= 2
                    && TrendResetRise < Threshold;
                if (!intervalOk)
                    problems.Add(new AlarmValidationProblem("trend", "alarms.validation.trend_interval_invalid"));
            }

            return problems;
        }

        /// <summary>Input bound on an operator's name. Long enough for a sentence, short enough for a journal record.</summary>
        public const int MaxNameLength = 80;
    }
}
