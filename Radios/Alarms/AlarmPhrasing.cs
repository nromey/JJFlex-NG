#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Flex.Smoothlake.FlexLib;

namespace Radios.Alarms
{
    /// <summary>
    /// Every sentence the alarm subsystem says or shows, assembled here from
    /// lexicon keys and real values — so a test can read the ASSEMBLED
    /// sentence, with degrees, decimals and negative numbers, rather than the
    /// template.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction design section 5 insists on: a reflected-power cut
    /// reports an action already taken; an operator alarm reports a measured
    /// condition and the action the operator should take. Nothing here says
    /// transmit was cut, because it was not. The action comes first, then
    /// the condition and its value, then any advice that can wait.
    /// </para>
    /// <para>
    /// The operator's name for an alarm is DATA: it is substituted into a
    /// placeholder, never parsed as a template, so it cannot carry markup or
    /// replace a safety sentence.
    /// </para>
    /// </remarks>
    public static class AlarmPhrasing
    {
        /// <summary>A value in its meter's units, at the precision the ear needs: whole tenths for temperature, hundredths for volts.</summary>
        public static string Value(double value, MeterUnits units)
        {
            if (double.IsNaN(value)) return "";
            string format = units switch
            {
                MeterUnits.Volts => "0.00",
                MeterUnits.Amps => "0.00",
                MeterUnits.DegreesC or MeterUnits.DegreesF => "0.#",
                MeterUnits.SWR => "0.0#",
                _ => "0.##",
            };
            return value.ToString(format, CultureInfo.CurrentCulture);
        }

        private static string Units(MeterUnits units) => MeterReading.UnitsText(units);

        /// <summary>Age in whole seconds, for "no reading for {age} seconds".</summary>
        public static string Age(double seconds) =>
            double.IsNaN(seconds) ? "?" : Math.Round(seconds).ToString("0", CultureInfo.CurrentCulture);

        private static MeterDescriptor MeterOf(AlarmEvent e) =>
            e.Observation?.Meter ?? new MeterDescriptor(-1, e.Definition.Selector.Name, e.Definition.Selector.Description,
                e.Definition.Selector.Source, e.Definition.Selector.SourceIndex, e.Definition.Selector.Units, 0, 0);

        /// <summary>
        /// Where on the supply the voltage is measured, in the few words a ham
        /// says: "before the fuse" and "after the fuse" on a 6300, "at the PA"
        /// and "at the CPU" on an 8600. One length, everywhere it is said.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>RULED by Noel 2026-10-01 07:17 and 07:18 (#566): "low supply
        /// voltage before THE fuse", and the same place phrase in preset names,
        /// in the normal warning and in the terse warning.</b> A terse warning
        /// is shortened by dropping the follow-up advice, never the place (see
        /// <see cref="Warning(AlarmEvent, bool)"/>). This SUPERSEDES the
        /// two-lengths part of his 2026-09-23 04:42 ruling, which gave terse a
        /// bare "before" and "after": assembled, "Supply voltage before is 11.90
        /// volts" left "before" with no object, and he judged that he had
        /// misread the question the earlier ruling answered.
        /// </para>
        /// <para>
        /// <b>NO COMMAS still stands from 2026-09-23.</b> The first cut
        /// (2026-09-22) put the phrase between commas, and every sentence that
        /// led with a preset NAME then ran into its verb at exactly the place the
        /// comma promised a pause: "Delete the Low supply voltage, before the fuse
        /// alarm?" With no comma in the phrase there is no collision to patch.
        /// </para>
        /// <para>
        /// <b>Keyed on the name AND the description together, not the name
        /// alone.</b> Both radios publish <c>+13.8A</c> and <c>+13.8B</c>: on
        /// the 6300 of the 2026-09-06 trace those are before and after the fuse,
        /// on the bench 8600 they are at the PA and at the CPU. A table keyed on
        /// the name would give one radio the other's place-phrase, which is the
        /// one outcome the fallback exists to prevent.
        /// </para>
        /// <para>
        /// <b>A meter this build has not met is named by the radio's own meter
        /// NAME, after the word "meter"</b> — "Low supply voltage on meter +13.8C
        /// is 11.90 volts" — rather than its description. "Meter" tells the
        /// listener what the unfamiliar code names (Sol's language pass,
        /// 2026-10-01). The description is where the place lives, but inside
        /// this frame it reads as the very sentence Noel first objected to
        /// ("Supply voltage Main radio input voltage before fuse is ..."); the
        /// description is still in the list row, the status and the editor. No
        /// physical place is claimed for a meter we have no evidence about.
        /// DRAFT: this fallback was not part of either ruling and is Noel's to
        /// confirm.
        /// </para>
        /// </remarks>
        public static string MeasurementPoint(MeterDescriptor m)
        {
            foreach (var (name, description, key) in KnownPoints)
            {
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(m.Description, description, StringComparison.OrdinalIgnoreCase))
                    return Lexicon.Get(key);
            }
            return Lexicon.Get("alarms.point.meter", ("meter", m.Name));
        }

        /// <summary>
        /// The spoken warning takes its terse frame when the operator's
        /// verbosity is Terse, and the normal one otherwise. Read at speak time,
        /// so the setting moving between two warnings changes the second.
        /// </summary>
        private static bool Terse => ScreenReaderOutput.CurrentVerbosity == VerbosityLevel.Terse;

        /// <summary>
        /// The supply meters whose inventories we hold: a 6300, from a trace of
        /// 2026-09-06, and the bench 8600, from the capture of 2026-09-07. Nothing here is a model-wide claim — a radio that
        /// publishes a supply meter under other words falls back to its own.
        /// </summary>
        private static readonly (string Name, string Description, string Key)[] KnownPoints =
        {
            ("+13.8A", "Main radio input voltage before fuse", "alarms.point.before_fuse"),
            ("+13.8B", "Main radio input voltage after fuse", "alarms.point.after_fuse"),
            ("+13.8A", "+13.8V at PA", "alarms.point.at_pa"),
            ("+13.8B", "+13.8V at CPU", "alarms.point.at_cpu"),
        };

        // ── the warnings ──

        /// <summary>The sentence for a firing, a reminder or a worsening, with the tx or rx action, at the operator's verbosity.</summary>
        public static string Warning(AlarmEvent e) => Warning(e, Terse);

        /// <summary>The same, with the length chosen by the caller — the testable form.</summary>
        /// <remarks>
        /// <para>
        /// <b>The action comes FIRST</b> wherever the alarm has one — "Release
        /// transmit now." or "Stay in receive." — so the first complete clause
        /// still works if the rest of the sentence is cut off (Sol's language
        /// pass, adopted 2026-10-01). That helps a partial delivery only; it does
        /// nothing for a sentence cancelled before its first word (#643), where
        /// the tone and the alarm list carry the warning.
        /// </para>
        /// <para>
        /// <b><paramref name="terse"/> picks a genuinely shorter frame</b> for a
        /// supply warning: the same place phrase, the same condition word and the
        /// same action, without the follow-up advice to have the supply path
        /// checked. Separate keys rather than a shorter place phrase, because one
        /// frame cannot serve both lengths. The PA warnings and the generic
        /// assembly have one length; nothing has yet asked for a terse form of
        /// them.
        /// </para>
        /// </remarks>
        public static string Warning(AlarmEvent e, bool terse)
        {
            AlarmDefinition def = e.Definition;
            MeterDescriptor meter = MeterOf(e);
            MeterUnits units = meter.Units;
            bool tx = e.Transmitting;
            string value = Value(e.Value, units);
            string change = Value(Math.Abs(e.Change), units);
            string interval = double.IsNaN(e.IntervalSeconds) ? "" : Math.Round(e.IntervalSeconds).ToString("0", CultureInfo.CurrentCulture);

            // A preset's sentences only while the definition still IS that
            // preset in the ways the sentences assume (finding 7); an edited
            // one takes the generic assembly below, which is true of any meter.
            switch (AlarmPresets.WordingApplies(def) ? def.PresetKey : "")
            {
                case AlarmPresets.PaTemperature:
                    return Lexicon.Get(tx ? "alarms.pa.high_tx" : "alarms.pa.high_rx", ("value", value));
                case AlarmPresets.PaRiseFromBaseline:
                    return Lexicon.Get(tx ? "alarms.pa.rise_from_baseline_tx" : "alarms.pa.rise_from_baseline_rx",
                        ("change", change), ("value", value));
                case AlarmPresets.PaRisingFast:
                    return Lexicon.Get(tx ? "alarms.pa.rising_fast_tx" : "alarms.pa.rising_fast_rx",
                        ("change", change), ("interval", interval), ("value", value));
                case AlarmPresets.VoltageLow:
                    return Lexicon.Get(terse
                            ? (tx ? "alarms.voltage.low_tx.terse" : "alarms.voltage.low_rx.terse")
                            : (tx ? "alarms.voltage.low_tx" : "alarms.voltage.low_rx"),
                        ("point", MeasurementPoint(meter)), ("value", value));
                case AlarmPresets.VoltageHigh:
                    return Lexicon.Get(terse
                            ? (tx ? "alarms.voltage.high_tx.terse" : "alarms.voltage.high_rx.terse")
                            : (tx ? "alarms.voltage.high_tx" : "alarms.voltage.high_rx"),
                        ("point", MeasurementPoint(meter)), ("value", value));
                case AlarmPresets.VoltageDrop:
                    return Lexicon.Get(terse
                            ? (tx ? "alarms.voltage.drop_tx.terse" : "alarms.voltage.drop_rx.terse")
                            : (tx ? "alarms.voltage.drop_tx" : "alarms.voltage.drop_rx"),
                        ("point", MeasurementPoint(meter)), ("change", change), ("value", value));
            }

            string key;
            if (e.Kind == AlarmEventKind.Reminder) key = "alarms.meter.reminder";
            else if (e.Kind == AlarmEventKind.Worsened) key = "alarms.meter.worsened";
            else if (def.Condition == AlarmCondition.Level && !float.IsNaN(e.Value) && !def.IsOnAlarmSide(e.Value))
            {
                // **The value stated is INSIDE the clear margin, so the firing
                // frame's comparison would be false of it (Astra's Track IJK2
                // review, blocker 3).** A warning is re-read before it is
                // spoken and carries the CURRENT reading, not the one it fired
                // on; an episode stays active inside its margin by design; so
                // "fired at 61" can be spoken when the reading is 59, and the
                // frame "is 59 degrees C, at or above 60 degrees C" asserts
                // something false of the number it states. The still-active
                // frame states the current reading and says what clears the
                // alarm instead — the same place the operator's question "why
                // is it still alarming at 59" is actually answered. Judged at
                // the meter's precision, as the monitor judges it, so the
                // sentence and the episode cannot disagree. A Fired event the
                // monitor itself emits is always on the alarm side and never
                // comes here; only a re-read does.
                key = def.Direction == AlarmDirection.AtOrAbove ? "alarms.meter.still_active_above" : "alarms.meter.still_active_below";
            }
            else key = def.Condition switch
            {
                AlarmCondition.RiseFromBaseline => def.Direction == AlarmDirection.AtOrAbove ? "alarms.meter.rise" : "alarms.meter.fall",
                AlarmCondition.RisingFast => "alarms.meter.trend",
                _ => def.Direction == AlarmDirection.AtOrAbove ? "alarms.meter.above" : "alarms.meter.below",
            };

            // The operator's name goes LAST so a name that happens to contain
            // "{value}" is inserted after every other placeholder is gone.
            string sentence = Lexicon.Get(key,
                ("meter", meter.Label), ("value", value), ("units", Units(units)),
                ("threshold", Value(def.Threshold, units)), ("clear", Value(def.ClearBoundary, units)),
                ("change", change), ("interval", interval),
                ("alarm", def.Name));

            // The action first, as in the preset frames, so it survives a cut.
            string action = Action(def, tx);
            return Tidy(action.Length == 0 ? sentence : action + " " + sentence);
        }

        /// <summary>The action clause for a generic alarm, from lexicon-owned templates. Empty for notify-only.</summary>
        public static string Action(AlarmDefinition def, bool transmitting) =>
            def.Action == AlarmActionClass.StopTransmit
                ? Lexicon.Get(transmitting ? "alarms.action.stop_transmit_tx" : "alarms.action.stop_transmit_rx")
                : "";

        /// <summary>A warning rebuilt from the CURRENT snapshot — what a deferred or retried alarm says after re-reading.</summary>
        public static AlarmEvent WarningFromSnapshot(AlarmSnapshot s)
        {
            MeterObservation? last = s.LastFresh;
            float value = last.HasValue ? last.Value.Value : float.NaN;
            float change = float.NaN;
            double interval = double.NaN;
            if (s.Definition.Condition == AlarmCondition.RiseFromBaseline && !float.IsNaN(s.BaselineValue) && !float.IsNaN(value))
                change = (float)s.Definition.WorseningBetween(s.BaselineValue, value);
            if (s.LastEvent != null && s.LastEvent.IsWarning)
            {
                if (s.Definition.Condition == AlarmCondition.RisingFast) { change = s.LastEvent.Change; interval = s.LastEvent.IntervalSeconds; }
            }
            return new AlarmEvent
            {
                Kind = AlarmEventKind.Reminder,
                Definition = s.Definition,
                EpisodeId = s.EpisodeId,
                Observation = last,
                Value = value,
                Threshold = s.Definition.Threshold,
                Change = change,
                Baseline = s.BaselineValue,
                IntervalSeconds = interval,
                Transmitting = s.Transmitting,
                ReminderReason = AlarmReminderReason.Interval,
            };
        }

        /// <summary>The reading stopped. Never says the condition cleared.</summary>
        public static string DataLost(AlarmEvent e)
        {
            MeterDescriptor meter = MeterOf(e);
            return Lexicon.Get(e.Transmitting ? "alarms.data_lost_tx" : "alarms.data_lost_rx",
                ("meter", meter.Label), ("age", Age(e.AgeSeconds)), ("alarm", e.Definition.Name));
        }

        public static string Ambiguous(AlarmEvent e) =>
            Lexicon.Get("alarms.data_ambiguous", ("meter", e.Definition.Selector.Name), ("alarm", e.Definition.Name));

        /// <summary>The clear rule was satisfied: a state update, no tone, no permission to transmit.</summary>
        public static string Cleared(AlarmEvent e)
        {
            MeterDescriptor meter = MeterOf(e);
            return Tidy(Lexicon.Get("alarms.cleared", ("meter", meter.Label),
                ("value", Value(e.Value, meter.Units)), ("units", Units(meter.Units)), ("alarm", e.Definition.Name)));
        }

        /// <summary>The preview, labelled a test, carrying no instruction that implies a real fault.</summary>
        public static string Preview(AlarmEvent e, string realSentence) =>
            Lexicon.Get("alarms.preview.sentence", ("sentence", realSentence), ("alarm", e.Definition.Name));

        public static string DeliveryUnavailable() => Lexicon.Get("alarms.delivery_unavailable");

        // ── status and the list ──

        /// <summary>The configured line in words: "at or above 60 degrees C", "a fall of 0.50 volts below the captured baseline".</summary>
        public static string Boundary(AlarmDefinition def)
        {
            MeterUnits units = def.Selector.Units;
            string threshold = Value(def.Threshold, units);
            string key = def.Condition switch
            {
                AlarmCondition.RiseFromBaseline => def.Direction == AlarmDirection.AtOrAbove ? "alarms.boundary.rise" : "alarms.boundary.fall",
                AlarmCondition.RisingFast => "alarms.boundary.trend",
                _ => def.Direction == AlarmDirection.AtOrAbove ? "alarms.boundary.at_or_above" : "alarms.boundary.at_or_below",
            };
            int seconds = (int)Math.Round((def.TrendIntervalMinSeconds + def.TrendIntervalMaxSeconds) / 2);
            return Tidy(Lexicon.Get(key, ("threshold", threshold), ("units", Units(units)),
                ("seconds", seconds.ToString(CultureInfo.CurrentCulture))));
        }

        /// <summary>The state of one alarm in WORDS for its list row.</summary>
        public static string StateWords(AlarmSnapshot s)
        {
            if (!s.Definition.Enabled) return Lexicon.Get("alarms.dialog.row_state_disabled");
            if (s.Resolution != MeterSelectorStatus.Resolved)
                return Lexicon.Get("alarms.dialog.row_state_unavailable", ("why", ResolutionWords(s.Resolution)));
            switch (s.Condition)
            {
                case AlarmConditionState.Active:
                    return s.Notification switch
                    {
                        AlarmNotificationState.Acknowledged => Lexicon.Get("alarms.dialog.row_state_active_acknowledged"),
                        AlarmNotificationState.Snoozed => Lexicon.Get("alarms.dialog.row_state_active_snoozed"),
                        _ => Lexicon.Get("alarms.dialog.row_state_active"),
                    };
                case AlarmConditionState.LastKnownActive:
                    return Lexicon.Get("alarms.dialog.row_state_last_known_active");
                case AlarmConditionState.WaitingForScope:
                    return Lexicon.Get("alarms.dialog.row_state_waiting_for_scope");
            }
            return s.Data switch
            {
                AlarmDataState.Waiting => Lexicon.Get("alarms.dialog.row_state_waiting"),
                AlarmDataState.Fresh or AlarmDataState.Recovering => Lexicon.Get("alarms.dialog.row_state_watching"),
                _ => Lexicon.Get("alarms.dialog.row_state_unavailable", ("why", DataWords(s.Data))),
            };
        }

        public static string DataWords(AlarmDataState d) => Lexicon.Get(d switch
        {
            AlarmDataState.Waiting => "alarms.data.waiting",
            AlarmDataState.Fresh => "alarms.data.fresh",
            AlarmDataState.Recovering => "alarms.data.recovering",
            AlarmDataState.Stale => "alarms.data.stale",
            AlarmDataState.Missing => "alarms.data.missing",
            _ => "alarms.data.ambiguous",
        });

        public static string ConditionWords(AlarmConditionState c) => Lexicon.Get(c switch
        {
            AlarmConditionState.Pending => "alarms.condition.pending",
            AlarmConditionState.Active => "alarms.condition.active",
            AlarmConditionState.LastKnownActive => "alarms.condition.last_known_active",
            AlarmConditionState.WaitingForScope => "alarms.condition.waiting_for_scope",
            _ => "alarms.condition.normal",
        });

        public static string ResolutionWords(MeterSelectorStatus r) => Lexicon.Get(r switch
        {
            MeterSelectorStatus.Resolved => "alarms.resolution.resolved",
            MeterSelectorStatus.Missing => "alarms.resolution.missing",
            MeterSelectorStatus.Ambiguous => "alarms.resolution.ambiguous",
            MeterSelectorStatus.SourceChanged => "alarms.resolution.source_changed",
            _ => "alarms.resolution.unit_changed",
        });

        /// <summary>One list row: name, meter, line, state — all in words.</summary>
        public static string Row(AlarmSnapshot s)
        {
            string meter = s.ResolvedMeter?.Label ?? s.Definition.Selector.Label;
            return Lexicon.Get("alarms.dialog.row", ("meter", meter),
                ("boundary", Boundary(s.Definition)), ("state", StateWords(s)), ("name", s.Definition.Name));
        }

        /// <summary>Read status: the four axes, the latest value and its age, the line, the baseline, the meter. Requested, so every verbosity.</summary>
        public static string Status(AlarmSnapshot s)
        {
            var sb = new StringBuilder();
            sb.Append(Lexicon.Get("alarms.status.line",
                ("enabled", Lexicon.Get(s.Definition.Enabled ? "alarms.state.enabled" : "alarms.state.disabled")),
                ("data", DataWords(s.Data)),
                ("condition", ConditionWords(s.Condition)),
                ("alarm", s.Definition.Name)));
            if (s.Notification != AlarmNotificationState.None)
            {
                sb.Append(' ').Append(Lexicon.Get("alarms.status.notification", ("notification", Lexicon.Get(s.Notification switch
                {
                    AlarmNotificationState.Acknowledged => "alarms.notification.acknowledged",
                    AlarmNotificationState.Snoozed => "alarms.notification.snoozed",
                    _ => "alarms.notification.unacknowledged",
                }))));
                if (s.Notification == AlarmNotificationState.Snoozed)
                    sb.Append(' ').Append(Lexicon.Get("alarms.status.snooze_remaining", ("seconds", Age(s.SnoozeRemainingSeconds))));
            }

            MeterUnits units = s.Definition.Selector.Units;
            if (s.LastFresh.HasValue)
                sb.Append(' ').Append(Lexicon.Get("alarms.status.latest",
                    ("value", Value(s.LastFresh.Value.Value, units)), ("units", Units(units)),
                    ("age", MeterInventory.DescribeAge(TimeSpan.FromSeconds(double.IsNaN(s.LastFreshAgeSeconds) ? 0 : s.LastFreshAgeSeconds)))));
            else
                sb.Append(' ').Append(Lexicon.Get("alarms.status.no_reading"));

            sb.Append(' ').Append(Lexicon.Get("alarms.status.line_level", ("boundary", Boundary(s.Definition))));

            if (s.Definition.Condition == AlarmCondition.RiseFromBaseline)
            {
                sb.Append(' ').Append(s.Baseline == AlarmBaselineState.Captured
                    ? Lexicon.Get("alarms.status.baseline", ("value", Value(s.BaselineValue, units)), ("units", Units(units)))
                    : Lexicon.Get("alarms.status.baseline_missing"));
            }

            sb.Append(' ').Append(Lexicon.Get("alarms.status.meter",
                ("meter", s.ResolvedMeter?.Label ?? s.Definition.Selector.Label),
                ("resolution", ResolutionWords(s.Resolution))));

            return Tidy(sb.ToString());
        }

        /// <summary>Read active alarms: the current active and unavailable summary, at every verbosity.</summary>
        public static string ActiveSummary(IReadOnlyList<AlarmSnapshot> all, bool connected,
            AlarmStoreState store, string storeProblem)
        {
            if (!connected) return Lexicon.Get("alarms.summary.disconnected");
            if (store == AlarmStoreState.Unavailable)
                return Lexicon.Get("alarms.summary.config_unavailable", ("problem", storeProblem));
            if (all.Count == 0) return Lexicon.Get("alarms.summary.none");

            var active = new List<string>();
            var lastKnown = new List<string>();
            int unavailable = 0;
            int notReady = 0;
            foreach (AlarmSnapshot s in all)
            {
                if (!s.Definition.Enabled) continue;
                MeterUnits units = s.Definition.Selector.Units;
                if (s.Condition == AlarmConditionState.Active)
                {
                    string value = s.LastFresh.HasValue ? Value(s.LastFresh.Value.Value, units) : "";
                    active.Add(Tidy(Lexicon.Get("alarms.summary.item",
                        ("value", value), ("units", value.Length == 0 ? "" : Units(units)), ("alarm", s.Definition.Name))));
                }
                else if (s.Condition == AlarmConditionState.LastKnownActive)
                {
                    // A last-known value is history, and it gets a sentence of
                    // its own rather than a place in the "active" count: "1 of 1
                    // alarms active" is false when the meter has stopped
                    // reporting (Sol's language pass, adopted 2026-10-01). A
                    // monitor that lost its meter outright keeps no reading, so
                    // that case has a frame without a value instead of a
                    // dangling "at".
                    lastKnown.Add(s.LastFresh.HasValue
                        ? Tidy(Lexicon.Get("alarms.summary.last_known",
                            ("value", Value(s.LastFresh.Value.Value, units)), ("units", Units(units)), ("alarm", s.Definition.Name)))
                        : Lexicon.Get("alarms.summary.last_known_no_value", ("alarm", s.Definition.Name)));
                }

                bool resolvedAndFed = s.Resolution == MeterSelectorStatus.Resolved
                    && s.Data is AlarmDataState.Fresh or AlarmDataState.Recovering;
                if (!resolvedAndFed && s.Data != AlarmDataState.Waiting)
                {
                    unavailable++;
                }
                else if (s.Data == AlarmDataState.Waiting
                         || s.Condition == AlarmConditionState.WaitingForScope
                         || (s.Definition.Condition == AlarmCondition.RiseFromBaseline && s.Baseline != AlarmBaselineState.Captured)
                         || (s.Definition.Condition == AlarmCondition.RisingFast
                             && s.LastEvent?.Kind is AlarmEventKind.TrendWarmingUp or AlarmEventKind.TrendInsufficientCoverage))
                {
                    // Enabled and fed, but it cannot judge its condition yet:
                    // no reading, no baseline, waiting for transmit, or a trend
                    // without a full window. "Not active" must never read as
                    // "watching successfully" (Astra's Track I review).
                    notReady++;
                }
            }

            // The order, chosen 2026-10-01 when last-known alarms left the
            // active count: what is active NOW, then what was active when its
            // reading stopped, then how many cannot be watched, then how many
            // are not ready. "N of M alarms active" counts only the alarms whose
            // current reading is on the bad side. With nothing active now but
            // something last-known, there is no head at all: "none active" would
            // claim more than a stopped meter can tell us, so the last-known
            // sentences lead. The unavailable count stays a count rather than
            // "It": it covers every alarm, and a last-known alarm whose meter is
            // merely waiting after a reconnect is counted as not ready instead.
            var parts = new List<string>();
            if (active.Count > 0)
                parts.Add(Lexicon.Get("alarms.summary.active", ("active", active.Count.ToString(CultureInfo.CurrentCulture)),
                    ("count", all.Count.ToString(CultureInfo.CurrentCulture)), ("list", string.Join("; ", active))));
            else if (lastKnown.Count == 0)
                parts.Add(Lexicon.Get("alarms.summary.quiet", ("count", all.Count.ToString(CultureInfo.CurrentCulture))));
            parts.AddRange(lastKnown);
            if (unavailable > 0)
                parts.Add(Lexicon.Get("alarms.summary.unavailable", ("unavailable", unavailable.ToString(CultureInfo.CurrentCulture))));
            if (notReady > 0)
                parts.Add(Lexicon.Get(notReady == 1 ? "alarms.summary.not_ready_one" : "alarms.summary.not_ready",
                    ("count", notReady.ToString(CultureInfo.CurrentCulture))));
            return Tidy(string.Join(" ", parts));
        }

        /// <summary>One history row's words, for a browsable recent-event list.</summary>
        public static string EventInWords(AlarmEvent e)
        {
            if (e.Detail == "preview") return Lexicon.Get("alarms.event.preview");
            MeterDescriptor meter = MeterOf(e);
            MeterUnits units = meter.Units;
            string alarm = e.Definition.Name;
            string value = Value(e.Value, units);
            string u = Units(units);
            switch (e.Kind)
            {
                case AlarmEventKind.Fired:
                case AlarmEventKind.Reminder:
                case AlarmEventKind.Worsened:
                    return Warning(e);
                case AlarmEventKind.Cleared: return Cleared(e);
                case AlarmEventKind.DataStale: return DataLost(e);
                case AlarmEventKind.DataAmbiguous: return Ambiguous(e);
                case AlarmEventKind.Acknowledged: return Lexicon.Get("alarms.acknowledged", ("alarm", alarm));
                case AlarmEventKind.Snoozed: return Lexicon.Get("alarms.snoozed", ("seconds", Age(e.SnoozeSeconds)), ("alarm", alarm));
                case AlarmEventKind.Resumed: return Lexicon.Get("alarms.resumed", ("alarm", alarm));
                case AlarmEventKind.Silenced: return Lexicon.Get("alarms.event.silenced", ("alarm", alarm));
                case AlarmEventKind.BaselineCaptured:
                    return Tidy(Lexicon.Get("alarms.baseline_captured", ("value", Value(e.Baseline, units)), ("units", u)));
                case AlarmEventKind.BaselineRefused:
                    return Lexicon.Get("alarms.baseline_refused", ("reason", BaselineRefusal(e.Detail)));
                case AlarmEventKind.FirstSample: return Tidy(Lexicon.Get("alarms.event.first_sample", ("value", value), ("units", u), ("alarm", alarm)));
                case AlarmEventKind.Pending: return Tidy(Lexicon.Get("alarms.event.pending", ("value", value), ("units", u), ("alarm", alarm)));
                case AlarmEventKind.DataResumed: return Lexicon.Get("alarms.event.data_resumed", ("age", Age(e.AgeSeconds)), ("alarm", alarm));
                case AlarmEventKind.DataMissing: return Lexicon.Get("alarms.event.data_missing", ("alarm", alarm));
                case AlarmEventKind.InvalidSample: return Lexicon.Get("alarms.event.invalid_sample", ("alarm", alarm));
                case AlarmEventKind.OutOfRangeSample: return Tidy(Lexicon.Get("alarms.event.out_of_range", ("value", value), ("units", u), ("alarm", alarm)));
                case AlarmEventKind.OldGenerationDiscarded: return Lexicon.Get("alarms.event.old_generation", ("alarm", alarm));
                case AlarmEventKind.SnoozeExpired: return Lexicon.Get("alarms.event.snooze_expired", ("alarm", alarm));
                case AlarmEventKind.BaselineInvalidated: return Lexicon.Get("alarms.event.baseline_invalidated", ("alarm", alarm));
                case AlarmEventKind.TrendWarmingUp: return Lexicon.Get("alarms.event.trend_warming_up", ("alarm", alarm));
                case AlarmEventKind.TrendInsufficientCoverage: return Lexicon.Get("alarms.event.trend_insufficient", ("why", e.Detail), ("alarm", alarm));
                case AlarmEventKind.TrendCovered: return Lexicon.Get("alarms.event.trend_covered", ("alarm", alarm));
                case AlarmEventKind.Enabled: return Lexicon.Get("alarms.event.enabled", ("alarm", alarm));
                case AlarmEventKind.Disabled: return Lexicon.Get("alarms.event.disabled", ("alarm", alarm));
                case AlarmEventKind.ConfigurationChanged:
                    return Lexicon.Get("alarms.event.configuration_changed", ("revision", e.Definition.Revision.ToString(CultureInfo.CurrentCulture)), ("alarm", alarm));
                case AlarmEventKind.ScopeLeft: return Lexicon.Get("alarms.event.scope_left", ("alarm", alarm));
                case AlarmEventKind.ScopeEntered: return Lexicon.Get("alarms.event.scope_entered", ("alarm", alarm));
                default: return alarm + ": " + e.Kind;
            }
        }

        /// <summary>The engine's refusal reason, in the operator's words.</summary>
        public static string BaselineRefusal(string detail) => detail switch
        {
            "in transmit" => Lexicon.Get("alarms.baseline_refused.in_transmit"),
            "not a baseline alarm" => Lexicon.Get("alarms.baseline_refused.not_applicable"),
            _ => Lexicon.Get("alarms.baseline_refused.no_fresh_sample"),
        };

        /// <summary>
        /// Collapse the gaps a unitless meter leaves behind — "5  ," reads as
        /// a stumble. Deliberately narrow: two spaces to one, a space before a
        /// comma or full stop removed.
        /// </summary>
        public static string Tidy(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Replace(" ,", ",").Replace(" .", ".").Replace(" ;", ";").Trim();
        }
    }
}
