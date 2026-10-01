#nullable enable
using System;
using System.Collections.Generic;

namespace Radios.Alarms
{
    /// <summary>Is there data to judge? Separate from whether the condition is met.</summary>
    public enum AlarmDataState
    {
        /// <summary>Enabled, connected, and no sample yet. Not stale, not healthy.</summary>
        Waiting,

        /// <summary>The last sample arrived inside the freshness allowance.</summary>
        Fresh,

        /// <summary>Samples have resumed after a loss; one more fresh one before unavailability is called resolved.</summary>
        Recovering,

        /// <summary>No sample inside the allowance. The numeric condition is not judged.</summary>
        Stale,

        /// <summary>The meter is not there: disconnected, detached, removed, or not yet published.</summary>
        Missing,

        /// <summary>The selector cannot name one meter on this connection.</summary>
        Ambiguous,
    }

    /// <summary>Whether the condition is met, on the data there is.</summary>
    public enum AlarmConditionState
    {
        Normal,

        /// <summary>A sustained trigger is counting.</summary>
        Pending,

        Active,

        /// <summary>An episode was active when the data stopped. Neither cleared nor confirmed.</summary>
        LastKnownActive,

        /// <summary>Transmit-only scope, and this client is not transmitting. Not healthy.</summary>
        WaitingForScope,
    }

    /// <summary>What the operator has done about the current episode.</summary>
    public enum AlarmNotificationState
    {
        /// <summary>No episode.</summary>
        None,
        Unacknowledged,
        Acknowledged,
        Snoozed,
    }

    /// <summary>For delta alarms: whether a baseline exists to measure from.</summary>
    public enum AlarmBaselineState
    {
        NotApplicable,
        NotCaptured,
        Captured,
    }

    /// <summary>
    /// The state machine for ONE alarm definition. Pure: every input carries
    /// its own monotonic instant, nothing here reads a clock, speaks, marshals
    /// or writes. Each call returns the events it concluded, in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Four axes, kept apart</b> (design section 2): <see cref="Definition"/>.Enabled
    /// is intent; <see cref="Data"/> is availability; <see cref="Condition"/> is
    /// whether the line is crossed on the data there is; <see cref="Notification"/>
    /// is what the operator has done about it. "Acknowledged" is never "safe",
    /// "enabled" is never "watching", and "no warning" is never "healthy".
    /// </para>
    /// <para>
    /// <b>Freshness is judged against receipt on the monotonic clock</b>, at
    /// receipt and on every watchdog tick, never from a cached value's stamp.
    /// A sample that arrives after a silence longer than the allowance is a
    /// gap even if no tick noticed it in between, so persistence and clear
    /// counts break either way.
    /// </para>
    /// <para>
    /// <b>Reminders ride fresh samples.</b> A tick can expire a snooze or
    /// declare data stale; it never produces a warning, because a warning
    /// spoken from a timer would recite a cached value as current.
    /// </para>
    /// <para>
    /// Reuses the PATTERNS of <see cref="ReflectedPowerRun"/> — a bounded
    /// time-stamped history, explicit judgement eligibility, streaks — without
    /// touching it: its 16-share window and per-transmission reset are wrong
    /// for a ninety-second thermal history.
    /// </para>
    /// </remarks>
    public sealed class AlarmMonitor
    {
        private readonly List<AlarmEvent> _out = new List<AlarmEvent>();

        private int _generation = 1;
        private bool _enabled;
        private bool _transmitting;

        private MeterObservation? _lastFresh;
        private int _freshSinceResume;

        private string _episodeId = "";
        private readonly List<MeterObservation> _pending = new List<MeterObservation>();
        private readonly List<MeterObservation> _clearing = new List<MeterObservation>();

        private float _lastAnnouncedValue = float.NaN;
        private long _lastWarningMs;
        private AlarmReminderReason _announceOnNextFresh = AlarmReminderReason.None;
        private long _snoozeUntilMs;
        private int _notificationRevision;

        private float _baselineValue = float.NaN;
        private long _baselineSequence;

        private enum TrendCoverage { Unknown, WarmingUp, Insufficient, Covered }
        private readonly List<(long Ms, float Value)> _history = new List<(long, float)>();
        private TrendCoverage _trend = TrendCoverage.Unknown;

        public AlarmMonitor(AlarmDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _enabled = definition.Enabled;
            Baseline = definition.Condition == AlarmCondition.RiseFromBaseline
                ? AlarmBaselineState.NotCaptured
                : AlarmBaselineState.NotApplicable;
        }

        public AlarmDefinition Definition { get; private set; }
        public AlarmDataState Data { get; private set; } = AlarmDataState.Waiting;
        public AlarmConditionState Condition { get; private set; } = AlarmConditionState.Normal;
        public AlarmNotificationState Notification { get; private set; } = AlarmNotificationState.None;
        public AlarmBaselineState Baseline { get; private set; }

        /// <summary>The captured baseline, or NaN.</summary>
        public float BaselineValue => _baselineValue;

        /// <summary>The last valid observation, or null.</summary>
        public MeterObservation? LastFresh => _lastFresh;

        /// <summary>The current or last-known episode, or empty.</summary>
        public string EpisodeId => _episodeId;

        public int ConnectionGeneration => _generation;
        public bool IsTransmitting => _transmitting;
        public bool Enabled => _enabled;

        /// <summary>The value the operator was last warned with, or NaN.</summary>
        public float LastAnnouncedValue => _lastAnnouncedValue;

        /// <summary>
        /// Bumped every time the operator's answer to the episode changes —
        /// acknowledge, snooze, resume, snooze expiry — and when an episode
        /// opens or closes. Every event carries the revision it was judged
        /// under, so delivery can tell "acknowledged BEFORE this warning,
        /// which the warning is allowed to override" from "acknowledged AFTER
        /// it, which withdraws it" (Astra's Track I review, finding 1).
        /// </summary>
        public int NotificationRevision => _notificationRevision;

        /// <summary>Seconds of snooze left at <paramref name="nowMs"/>, or zero.</summary>
        public double SnoozeRemainingSeconds(long nowMs) =>
            Notification == AlarmNotificationState.Snoozed ? Math.Max(0, _snoozeUntilMs - nowMs) / 1000.0 : 0;

        /// <summary>True while an episode exists, whether the data is fresh or last-known.</summary>
        public bool HasEpisode => Condition is AlarmConditionState.Active or AlarmConditionState.LastKnownActive;

        // ── inputs ──

        /// <summary>A meter delivered a value. Only measured or replayed observations are judged.</summary>
        public IReadOnlyList<AlarmEvent> Observe(in MeterObservation obs, long nowMs)
        {
            _out.Clear();
            if (obs.Provenance == ObservationProvenance.Preview) return Snapshot();
            if (obs.ConnectionGeneration != _generation)
            {
                Emit(AlarmEventKind.OldGenerationDiscarded, nowMs, obs, obs.Value,
                    detail: "generation " + obs.ConnectionGeneration + " against " + _generation);
                return Snapshot();
            }
            if (!_enabled) return Snapshot();

            if (!obs.IsValid)
            {
                Emit(AlarmEventKind.InvalidSample, nowMs, obs, obs.Value, detail: obs.Validity.ToString());
                BreakPending();
                _clearing.Clear();
                return Snapshot();
            }

            bool gap = false;
            if (_lastFresh.HasValue
                && obs.ReceiptMonotonicMs - _lastFresh.Value.ReceiptMonotonicMs > AllowanceMs)
            {
                gap = true;
                if (Data is AlarmDataState.Fresh or AlarmDataState.Recovering)
                    MarkStale(nowMs, obs.ReceiptMonotonicMs);
            }

            switch (Data)
            {
                case AlarmDataState.Waiting:
                    Data = AlarmDataState.Fresh;
                    Emit(AlarmEventKind.FirstSample, nowMs, obs, obs.Value);
                    break;
                case AlarmDataState.Stale:
                case AlarmDataState.Missing:
                case AlarmDataState.Ambiguous:
                    Data = AlarmDataState.Recovering;
                    _freshSinceResume = 1;
                    Emit(AlarmEventKind.DataResumed, nowMs, obs, obs.Value,
                        ageSeconds: _lastFresh.HasValue ? _lastFresh.Value.AgeSeconds(obs.ReceiptMonotonicMs) : double.NaN);
                    break;
                case AlarmDataState.Recovering:
                    if (++_freshSinceResume >= 2) Data = AlarmDataState.Fresh;
                    break;
            }

            _lastFresh = obs;
            if (obs.OutOfRange)
                Emit(AlarmEventKind.OutOfRangeSample, nowMs, obs, obs.Value,
                    detail: "advertised range " + obs.Meter.Low + " to " + obs.Meter.High);

            if (gap)
            {
                BreakPending();
                _clearing.Clear();
            }

            if (Definition.Scope == AlarmScope.TransmitOnly && !_transmitting)
            {
                if (Condition is AlarmConditionState.Normal or AlarmConditionState.Pending)
                {
                    BreakPending();
                    Condition = AlarmConditionState.WaitingForScope;
                }
                return Snapshot();
            }

            switch (Definition.Condition)
            {
                case AlarmCondition.Level:
                    Judge(obs, nowMs,
                        onAlarmSide: Definition.IsOnAlarmSide(obs.Value),
                        beyondClear: Definition.IsBeyondClear(obs.Value),
                        change: float.NaN, interval: double.NaN);
                    break;

                case AlarmCondition.RiseFromBaseline:
                    if (Baseline != AlarmBaselineState.Captured) break;   // unavailable, not zero
                    {
                        float delta = (float)Definition.WorseningBetween(_baselineValue, obs.Value);
                        Judge(obs, nowMs,
                            onAlarmSide: delta >= Definition.Threshold,
                            beyondClear: delta <= Definition.Threshold - Definition.Hysteresis,
                            change: delta, interval: double.NaN);
                    }
                    break;

                case AlarmCondition.RisingFast:
                    JudgeTrend(obs, nowMs);
                    break;
            }

            return Snapshot();
        }

        /// <summary>The 250 ms watchdog. Transitions only: staleness, snooze expiry. Never a warning.</summary>
        public IReadOnlyList<AlarmEvent> Tick(long nowMs)
        {
            _out.Clear();
            if (!_enabled) return Snapshot();

            if (Data is AlarmDataState.Fresh or AlarmDataState.Recovering
                && _lastFresh.HasValue
                && nowMs - _lastFresh.Value.ReceiptMonotonicMs > AllowanceMs)
            {
                MarkStale(nowMs, nowMs);
            }

            if (Notification == AlarmNotificationState.Snoozed && nowMs >= _snoozeUntilMs)
            {
                Notification = AlarmNotificationState.Unacknowledged;
                _notificationRevision++;
                Emit(AlarmEventKind.SnoozeExpired, nowMs, null, float.NaN);
            }

            return Snapshot();
        }

        /// <summary>This client keyed or unkeyed. Scope edges reset dwell and trend coverage; a transition while
        /// an episode is active queues a fresh warning for the next sample.</summary>
        public IReadOnlyList<AlarmEvent> SetTransmit(bool transmitting, long nowMs)
        {
            _out.Clear();
            if (transmitting == _transmitting) return Snapshot();
            _transmitting = transmitting;

            if (Definition.Scope == AlarmScope.TransmitOnly)
            {
                BreakPending();
                _history.Clear();
                _trend = TrendCoverage.Unknown;
                if (transmitting)
                {
                    Emit(AlarmEventKind.ScopeEntered, nowMs, null, float.NaN);
                }
                else
                {
                    if (Condition is AlarmConditionState.Normal or AlarmConditionState.Pending)
                        Condition = AlarmConditionState.WaitingForScope;
                    Emit(AlarmEventKind.ScopeLeft, nowMs, null, float.NaN);
                }
            }

            if (transmitting && HasEpisode)
                _announceOnNextFresh = AlarmReminderReason.TransmitResumed;

            return Snapshot();
        }

        /// <summary>A new connection: observations, windows, baselines, acknowledgement and snooze go; the
        /// definition and the last-known episode stay.</summary>
        public IReadOnlyList<AlarmEvent> NewConnection(int generation, long nowMs)
        {
            _out.Clear();
            _generation = generation;
            LoseData(nowMs, "new connection generation " + generation, AlarmDataState.Waiting);
            InvalidateBaseline(nowMs, "reconnect");
            return Snapshot();
        }

        /// <summary>The meter went away on this connection (detach, removal, disconnect).</summary>
        public IReadOnlyList<AlarmEvent> MarkMissing(long nowMs, string why)
        {
            _out.Clear();
            if (Data != AlarmDataState.Missing) LoseData(nowMs, why, AlarmDataState.Missing);
            if (Baseline == AlarmBaselineState.Captured) InvalidateBaseline(nowMs, why);
            return Snapshot();
        }

        /// <summary>The selector cannot name one meter on this connection.</summary>
        public IReadOnlyList<AlarmEvent> MarkAmbiguous(long nowMs, string why)
        {
            _out.Clear();
            if (Data != AlarmDataState.Ambiguous)
            {
                LoseData(nowMs, why, AlarmDataState.Ambiguous);
                Emit(AlarmEventKind.DataAmbiguous, nowMs, null, float.NaN, detail: why);
            }
            if (Baseline == AlarmBaselineState.Captured) InvalidateBaseline(nowMs, why);
            return Snapshot();
        }

        public IReadOnlyList<AlarmEvent> Acknowledge(long nowMs)
        {
            _out.Clear();
            if (!HasEpisode) return Snapshot();
            Notification = AlarmNotificationState.Acknowledged;
            _notificationRevision++;
            _snoozeUntilMs = 0;
            Emit(AlarmEventKind.Acknowledged, nowMs, null, float.NaN);
            return Snapshot();
        }

        public IReadOnlyList<AlarmEvent> Snooze(double seconds, long nowMs)
        {
            _out.Clear();
            if (!HasEpisode) return Snapshot();
            Notification = AlarmNotificationState.Snoozed;
            _notificationRevision++;
            _snoozeUntilMs = nowMs + (long)(seconds * 1000);
            Emit(AlarmEventKind.Snoozed, nowMs, null, float.NaN, snoozeSeconds: seconds);
            return Snapshot();
        }

        /// <summary>End acknowledgement or snooze now, and repeat the current warning when the data is fresh.</summary>
        public IReadOnlyList<AlarmEvent> Resume(long nowMs)
        {
            _out.Clear();
            if (!HasEpisode || Notification == AlarmNotificationState.Unacknowledged) return Snapshot();
            Notification = AlarmNotificationState.Unacknowledged;
            _notificationRevision++;
            _snoozeUntilMs = 0;
            Emit(AlarmEventKind.Resumed, nowMs, null, float.NaN);

            if (Condition == AlarmConditionState.Active
                && Data is AlarmDataState.Fresh or AlarmDataState.Recovering
                && _lastFresh.HasValue
                && nowMs - _lastFresh.Value.ReceiptMonotonicMs <= AllowanceMs)
            {
                _lastWarningMs = nowMs;
                Emit(AlarmEventKind.Reminder, nowMs, _lastFresh, _lastFresh.Value.Value,
                    reminderReason: AlarmReminderReason.OperatorResumed, change: LastChange(_lastFresh.Value));
            }
            return Snapshot();
        }

        /// <summary>
        /// The last warning for the current episode did not reach the operator
        /// — the dispatch queue refused it, the speech layer let it go, or the
        /// reader took nothing — so the next fresh sample on the alarm side
        /// warns again, inside the reminder interval and whatever the
        /// acknowledgement state, because a warning nobody heard is not a
        /// repeat (Astra's Track I review, findings 4 and 5).
        /// </summary>
        /// <remarks>
        /// A flag rather than an event on purpose: the case that needs it most
        /// is the dispatch queue being full, which is exactly when an event
        /// could not be posted. The trace at the caller says what happened; the
        /// re-warning carries <see cref="AlarmReminderReason.DeliveryRetried"/>
        /// so the record says why it was said again.
        /// </remarks>
        public IReadOnlyList<AlarmEvent> WarningNotDelivered(long nowMs)
        {
            _out.Clear();
            if (HasEpisode && _enabled) _announceOnNextFresh = AlarmReminderReason.DeliveryRetried;
            return Snapshot();
        }

        /// <summary>Capture the delta baseline from the last FRESH sample, in receive only. Never at key-down,
        /// never repeated by unkeying, refused with a reason otherwise.</summary>
        public IReadOnlyList<AlarmEvent> CaptureBaseline(long nowMs)
        {
            _out.Clear();
            string? refusal = null;
            if (Definition.Condition != AlarmCondition.RiseFromBaseline) refusal = "not a baseline alarm";
            else if (_transmitting) refusal = "in transmit";
            else if (!_lastFresh.HasValue) refusal = "no sample yet";
            else if (Data is AlarmDataState.Stale or AlarmDataState.Missing or AlarmDataState.Ambiguous
                     || nowMs - _lastFresh.Value.ReceiptMonotonicMs > AllowanceMs)
                refusal = "no fresh sample";

            if (refusal != null)
            {
                Emit(AlarmEventKind.BaselineRefused, nowMs, null, float.NaN, detail: refusal);
                return Snapshot();
            }

            MeterObservation from = _lastFresh!.Value;
            if (HasEpisode) CloseEpisode(AlarmEventKind.ConfigurationChanged, nowMs, "baseline recaptured");
            _baselineValue = from.Value;
            _baselineSequence = from.Sequence;
            Baseline = AlarmBaselineState.Captured;
            Emit(AlarmEventKind.BaselineCaptured, nowMs, from, from.Value, baseline: from.Value);
            return Snapshot();
        }

        /// <summary>Enabled intent. Enabling reacquires: the next sample is the first, and an already-high one fires.</summary>
        public IReadOnlyList<AlarmEvent> SetEnabled(bool enabled, long nowMs)
        {
            _out.Clear();
            if (enabled == _enabled) return Snapshot();
            _enabled = enabled;
            Definition = Definition with { Enabled = enabled };
            if (enabled)
            {
                _lastFresh = null;
                _freshSinceResume = 0;
                Data = AlarmDataState.Waiting;
                Condition = AlarmConditionState.Normal;
                Emit(AlarmEventKind.Enabled, nowMs, null, float.NaN);
            }
            else
            {
                if (HasEpisode) CloseEpisode(AlarmEventKind.Disabled, nowMs, "disabled");
                else Emit(AlarmEventKind.Disabled, nowMs, null, float.NaN);
                BreakPending();
                _clearing.Clear();
                Condition = AlarmConditionState.Normal;
            }
            return Snapshot();
        }

        /// <summary>A saved edit. The old episode closes as configuration changed — never as cleared — and the
        /// next fresh sample is judged under the new definition.</summary>
        public IReadOnlyList<AlarmEvent> Replace(AlarmDefinition definition, long nowMs)
        {
            _out.Clear();
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            AlarmDefinition old = Definition;
            Definition = definition;
            _enabled = definition.Enabled;

            if (HasEpisode) CloseEpisode(AlarmEventKind.ConfigurationChanged, nowMs, "revision " + definition.Revision);
            else Emit(AlarmEventKind.ConfigurationChanged, nowMs, null, float.NaN, detail: "revision " + definition.Revision);

            BreakPending();
            _clearing.Clear();
            _history.Clear();
            _trend = TrendCoverage.Unknown;
            if (Condition is AlarmConditionState.Pending) Condition = AlarmConditionState.Normal;

            bool selectorChanged = !old.Selector.Equals(definition.Selector);
            if (definition.Condition == AlarmCondition.RiseFromBaseline)
            {
                if (Baseline == AlarmBaselineState.NotApplicable) Baseline = AlarmBaselineState.NotCaptured;
                if (selectorChanged || old.Condition != AlarmCondition.RiseFromBaseline)
                    InvalidateBaseline(nowMs, "definition changed");
            }
            else
            {
                if (Baseline == AlarmBaselineState.Captured) InvalidateBaseline(nowMs, "definition changed");
                Baseline = AlarmBaselineState.NotApplicable;
            }
            if (selectorChanged)
            {
                _lastFresh = null;
                _freshSinceResume = 0;
                Data = AlarmDataState.Waiting;
            }
            return Snapshot();
        }

        // ── judgement ──

        private long AllowanceMs => (long)(Definition.FreshnessAllowanceSeconds * 1000);

        private void Judge(in MeterObservation obs, long nowMs, bool onAlarmSide, bool beyondClear,
            float change, double interval)
        {
            switch (Condition)
            {
                case AlarmConditionState.Normal:
                case AlarmConditionState.Pending:
                case AlarmConditionState.WaitingForScope:
                    if (!onAlarmSide)
                    {
                        BreakPending();
                        Condition = AlarmConditionState.Normal;
                        return;
                    }
                    if (Definition.Persistence == AlarmPersistence.FirstFreshSample)
                    {
                        Fire(obs, nowMs, 1, change, interval);
                        return;
                    }
                    _pending.Add(obs);
                    if (Condition != AlarmConditionState.Pending)
                    {
                        Condition = AlarmConditionState.Pending;
                        Emit(AlarmEventKind.Pending, nowMs, obs, obs.Value, change: change);
                    }
                    if (_pending.Count >= Definition.SustainedCount
                        && obs.ReceiptMonotonicMs - _pending[0].ReceiptMonotonicMs >= Definition.SustainedSeconds * 1000)
                    {
                        Fire(obs, nowMs, _pending.Count, change, interval);
                    }
                    return;

                case AlarmConditionState.Active:
                case AlarmConditionState.LastKnownActive:
                    if (beyondClear)
                    {
                        _clearing.Add(obs);
                        if (_clearing.Count >= Definition.ClearSamples
                            && obs.ReceiptMonotonicMs - _clearing[0].ReceiptMonotonicMs >= Definition.ClearSeconds * 1000)
                        {
                            string episode = _episodeId;
                            Condition = AlarmConditionState.Normal;
                            Notification = AlarmNotificationState.None;
                            _snoozeUntilMs = 0;
                            _announceOnNextFresh = AlarmReminderReason.None;
                            _clearing.Clear();
                            BreakPending();
                            _lastAnnouncedValue = float.NaN;
                            Emit(AlarmEventKind.Cleared, nowMs, obs, obs.Value, change: change, episode: episode);
                            _episodeId = "";
                        }
                        return;
                    }

                    _clearing.Clear();
                    if (!onAlarmSide)
                    {
                        // In the hysteresis band: still active, nothing to say.
                        if (Condition == AlarmConditionState.LastKnownActive) Condition = AlarmConditionState.Active;
                        return;
                    }

                    if (Condition == AlarmConditionState.LastKnownActive)
                    {
                        Condition = AlarmConditionState.Active;
                        Warn(AlarmEventKind.Reminder, obs, nowMs, AlarmReminderReason.DataResumed, change, interval);
                        return;
                    }

                    if (Definition.WorseningStep > 0 && !float.IsNaN(_lastAnnouncedValue)
                        && Definition.WorseningBetween(_lastAnnouncedValue, obs.Value) >= Definition.WorseningStep)
                    {
                        Warn(AlarmEventKind.Worsened, obs, nowMs, AlarmReminderReason.None, change, interval);
                        return;
                    }

                    if (_announceOnNextFresh != AlarmReminderReason.None)
                    {
                        AlarmReminderReason reason = _announceOnNextFresh;
                        Warn(AlarmEventKind.Reminder, obs, nowMs, reason, change, interval);
                        return;
                    }

                    if (Notification == AlarmNotificationState.Unacknowledged
                        && Definition.ReminderIntervalSeconds > 0
                        && nowMs - _lastWarningMs >= Definition.ReminderIntervalSeconds * 1000)
                    {
                        Warn(AlarmEventKind.Reminder, obs, nowMs, AlarmReminderReason.Interval, change, interval);
                    }
                    return;
            }
        }

        private void Fire(in MeterObservation obs, long nowMs, int samples, float change, double interval)
        {
            _episodeId = Guid.NewGuid().ToString("N").Substring(0, 12);
            Condition = AlarmConditionState.Active;
            Notification = AlarmNotificationState.Unacknowledged;
            _notificationRevision++;
            _snoozeUntilMs = 0;
            _announceOnNextFresh = AlarmReminderReason.None;
            BreakPending();
            _clearing.Clear();
            _lastAnnouncedValue = obs.Value;
            _lastWarningMs = nowMs;
            Emit(AlarmEventKind.Fired, nowMs, obs, obs.Value, change: change, interval: interval,
                persistenceSamples: samples);
        }

        private void Warn(AlarmEventKind kind, in MeterObservation obs, long nowMs,
            AlarmReminderReason reason, float change, double interval)
        {
            _announceOnNextFresh = AlarmReminderReason.None;
            _lastAnnouncedValue = obs.Value;
            _lastWarningMs = nowMs;
            Emit(kind, nowMs, obs, obs.Value, change: change, interval: interval, reminderReason: reason);
        }

        private float LastChange(in MeterObservation obs)
        {
            if (Definition.Condition == AlarmCondition.RiseFromBaseline && Baseline == AlarmBaselineState.Captured)
                return (float)Definition.WorseningBetween(_baselineValue, obs.Value);
            return float.NaN;
        }

        // ── the optional trend ──

        private void JudgeTrend(in MeterObservation obs, long nowMs)
        {
            long windowMs = (long)(Definition.TrendWindowSeconds * 1000);
            long bandMs = (long)(Definition.TrendBandSeconds * 1000);
            long windowStart = obs.ReceiptMonotonicMs - windowMs;

            _history.Add((obs.ReceiptMonotonicMs, obs.Value));
            // Keep one sample from before the window, so "was there data at the
            // window's start" can be answered, and drop the rest.
            int firstInside = _history.FindIndex(h => h.Ms >= windowStart);
            int keepFrom = firstInside <= 0 ? 0 : firstInside - 1;
            if (keepFrom > 0) _history.RemoveRange(0, keepFrom);

            TrendCoverage coverage;
            float rise = float.NaN;
            double interval = double.NaN;
            string detail = "";

            if (_history.Count == 0 || _history[0].Ms > windowStart)
            {
                coverage = TrendCoverage.WarmingUp;
                detail = "fewer than " + Definition.TrendWindowSeconds + " s of observations";
            }
            else
            {
                coverage = TrendCoverage.Covered;
                for (int i = 1; i < _history.Count; i++)
                {
                    if (_history[i].Ms - _history[i - 1].Ms > AllowanceMs)
                    {
                        coverage = TrendCoverage.Insufficient;
                        detail = "a gap of " + ((_history[i].Ms - _history[i - 1].Ms) / 1000.0).ToString("0.0",
                            System.Globalization.CultureInfo.InvariantCulture) + " s inside the window";
                        break;
                    }
                }

                if (coverage == TrendCoverage.Covered)
                {
                    var early = new List<(long Ms, float Value)>();
                    var late = new List<(long Ms, float Value)>();
                    foreach (var h in _history)
                    {
                        if (h.Ms >= windowStart && h.Ms <= windowStart + bandMs) early.Add(h);
                        if (h.Ms >= obs.ReceiptMonotonicMs - bandMs && h.Ms <= obs.ReceiptMonotonicMs) late.Add(h);
                    }
                    if (early.Count < Definition.TrendMinSamplesPerBand || late.Count < Definition.TrendMinSamplesPerBand)
                    {
                        coverage = TrendCoverage.Insufficient;
                        detail = early.Count + " and " + late.Count + " samples in the endpoint bands, "
                               + Definition.TrendMinSamplesPerBand + " needed";
                    }
                    else
                    {
                        (long earlyMs, float earlyValue) = Median(early);
                        (long lateMs, float lateValue) = Median(late);
                        interval = (lateMs - earlyMs) / 1000.0;
                        if (interval < Definition.TrendIntervalMinSeconds || interval > Definition.TrendIntervalMaxSeconds)
                        {
                            coverage = TrendCoverage.Insufficient;
                            detail = "median observation times " + interval.ToString("0.0",
                                System.Globalization.CultureInfo.InvariantCulture) + " s apart";
                        }
                        else
                        {
                            rise = lateValue - earlyValue;
                        }
                    }
                }
            }

            if (coverage != _trend)
            {
                TrendCoverage previous = _trend;
                _trend = coverage;
                switch (coverage)
                {
                    case TrendCoverage.WarmingUp:
                        Emit(AlarmEventKind.TrendWarmingUp, nowMs, obs, obs.Value, detail: detail);
                        break;
                    case TrendCoverage.Insufficient:
                        Emit(AlarmEventKind.TrendInsufficientCoverage, nowMs, obs, obs.Value, detail: detail);
                        break;
                    case TrendCoverage.Covered:
                        if (previous == TrendCoverage.Insufficient)
                            Emit(AlarmEventKind.TrendCovered, nowMs, obs, obs.Value);
                        break;
                }
            }

            if (coverage != TrendCoverage.Covered) return;

            Judge(obs, nowMs,
                onAlarmSide: rise >= Definition.Threshold,
                beyondClear: rise <= Definition.TrendResetRise,
                change: rise, interval: interval);
        }

        /// <summary>Median value and median observation time of a band, each on its own axis.</summary>
        private static (long Ms, float Value) Median(List<(long Ms, float Value)> band)
        {
            var values = new List<float>(band.Count);
            var times = new List<long>(band.Count);
            foreach (var b in band) { values.Add(b.Value); times.Add(b.Ms); }
            values.Sort();
            times.Sort();
            int n = band.Count;
            float v = n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2f;
            long t = n % 2 == 1 ? times[n / 2] : (times[n / 2 - 1] + times[n / 2]) / 2;
            return (t, v);
        }

        // ── state changes shared by several inputs ──

        private void MarkStale(long nowMs, long judgedAtMs)
        {
            bool wasActive = HasEpisode || Condition == AlarmConditionState.Pending;
            double age = _lastFresh.HasValue ? _lastFresh.Value.AgeSeconds(judgedAtMs) : double.NaN;
            Data = AlarmDataState.Stale;
            _freshSinceResume = 0;
            BreakPending();
            _clearing.Clear();
            if (Condition == AlarmConditionState.Active) Condition = AlarmConditionState.LastKnownActive;
            Emit(AlarmEventKind.DataStale, nowMs, null, float.NaN, ageSeconds: age, wasActive: wasActive);
        }

        /// <summary>
        /// The data is gone. A new connection leaves the monitor WAITING for the
        /// meter to be published again; a detach or removal leaves it MISSING.
        /// Either way the episode is preserved as last-known and the
        /// acknowledgement and snooze are cleared — a warning that returns
        /// after a loss is a new warning, whatever the operator said about the
        /// old one.
        /// </summary>
        private void LoseData(long nowMs, string why, AlarmDataState resulting)
        {
            bool wasActive = HasEpisode;
            double age = _lastFresh.HasValue ? _lastFresh.Value.AgeSeconds(nowMs) : double.NaN;
            _lastFresh = null;
            _freshSinceResume = 0;
            Data = resulting;
            BreakPending();
            _clearing.Clear();
            _history.Clear();
            _trend = TrendCoverage.Unknown;
            Notification = AlarmNotificationState.None;
            _snoozeUntilMs = 0;
            _announceOnNextFresh = AlarmReminderReason.None;
            if (Condition == AlarmConditionState.Active) Condition = AlarmConditionState.LastKnownActive;
            else if (Condition is AlarmConditionState.Pending or AlarmConditionState.WaitingForScope)
                Condition = AlarmConditionState.Normal;
            Emit(AlarmEventKind.DataMissing, nowMs, null, float.NaN, ageSeconds: age, wasActive: wasActive, detail: why);
        }

        private void InvalidateBaseline(long nowMs, string why)
        {
            if (Baseline != AlarmBaselineState.Captured) return;
            Baseline = AlarmBaselineState.NotCaptured;
            _baselineValue = float.NaN;
            _baselineSequence = 0;
            Emit(AlarmEventKind.BaselineInvalidated, nowMs, null, float.NaN, detail: why);
        }

        private void CloseEpisode(AlarmEventKind kind, long nowMs, string detail)
        {
            string episode = _episodeId;
            Condition = AlarmConditionState.Normal;
            Notification = AlarmNotificationState.None;
            _snoozeUntilMs = 0;
            _announceOnNextFresh = AlarmReminderReason.None;
            _lastAnnouncedValue = float.NaN;
            _clearing.Clear();
            Emit(kind, nowMs, null, float.NaN, detail: detail, episode: episode);
            _episodeId = "";
        }

        private void BreakPending()
        {
            _pending.Clear();
            if (Condition == AlarmConditionState.Pending) Condition = AlarmConditionState.Normal;
        }

        private void Emit(AlarmEventKind kind, long nowMs, MeterObservation? obs, float value,
            float change = float.NaN, double interval = double.NaN, double ageSeconds = double.NaN,
            bool wasActive = false, AlarmReminderReason reminderReason = AlarmReminderReason.None,
            double snoozeSeconds = 0, int persistenceSamples = 0, string detail = "",
            float baseline = float.NaN, string? episode = null)
        {
            _out.Add(new AlarmEvent
            {
                Kind = kind,
                Definition = Definition,
                EpisodeId = episode ?? _episodeId,
                AtMs = nowMs,
                Observation = obs,
                Value = value,
                Threshold = Definition.Threshold,
                Change = change,
                Baseline = float.IsNaN(baseline) ? _baselineValue : baseline,
                IntervalSeconds = interval,
                AgeSeconds = ageSeconds,
                Transmitting = _transmitting,
                WasActive = wasActive,
                ReminderReason = reminderReason,
                SnoozeSeconds = snoozeSeconds,
                PersistenceSamples = persistenceSamples,
                Detail = detail,
                NotificationRevision = _notificationRevision,
            });
        }

        private IReadOnlyList<AlarmEvent> Snapshot() => _out.Count == 0
            ? Array.Empty<AlarmEvent>()
            : _out.ToArray();
    }
}
