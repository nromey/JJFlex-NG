#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Flex.Smoothlake.FlexLib;

namespace Radios.Alarms
{
    /// <summary>One entry in the Add flow's preset picker: a shipped preset for a discovered meter, or the operator's own.</summary>
    public sealed record PresetChoice(
        string Label,
        PresetChoiceKind Kind,
        AlarmDefinition? Definition,
        string Problem,
        OperatorPreset? Operator = null)
    {
        public bool IsAvailable => Definition != null || Kind == PresetChoiceKind.None;
    }

    public enum PresetChoiceKind { None, Shipped, Operator }

    /// <summary>One meter as the editor's searchable list shows it.</summary>
    public sealed record MeterChoice(MeterDescriptor Meter, string Label, int DuplicateCount);

    /// <summary>
    /// Everything the Operator alarms dialog decides, with no window in it —
    /// rows, details, actions and their spoken receipts, the editor's fields
    /// and validation, presets shipped and the operator's own — so it can be
    /// tested on a live desk without constructing a window.
    /// </summary>
    /// <remarks>
    /// The window is a thin shell over this: it puts the rows in a list, the
    /// detail text in one read-only field, the actions on buttons in the
    /// design's tab order, and speaks the receipts this returns. Standard
    /// controls announce themselves; this speaks only what they cannot.
    /// </remarks>
    public sealed class OperatorAlarmsModel
    {
        private readonly AlarmService _service;
        private readonly OperatorPresetStore? _presets;
        private readonly Func<string> _newId;

        public OperatorAlarmsModel(AlarmService service, OperatorPresetStore? presets, Func<string>? newId = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _presets = presets;
            _newId = newId ?? (() => Guid.NewGuid().ToString("N"));
        }

        public AlarmService Service => _service;

        /// <summary>A model over no radio at all, for a window built with nothing attached.</summary>
        public static OperatorAlarmsModel Detached() =>
            new OperatorAlarmsModel(new AlarmService(new DormantAlarmFeed(), null, new SystemAlarmClock(), null, startWatchdog: false), null);

        // ── the list ──

        public IReadOnlyList<AlarmSnapshot> Rows() => _service.Snapshot();

        public string RowText(AlarmSnapshot s) => AlarmPhrasing.Row(s);

        /// <summary>The status line under the list: connected and how many, no radio, or the configuration problem.</summary>
        public string StatusText()
        {
            if (!_service.IsConnected) return Lexicon.Get("alarms.dialog.no_radio");
            if (_service.StoreState == AlarmStoreState.Unavailable)
                return Lexicon.Get("alarms.dialog.config_unavailable", ("problem", _service.StoreProblem));
            return _service.Snapshot().Count == 0 ? Lexicon.Get("alarms.dialog.empty") : "";
        }

        /// <summary>
        /// The one detail field: latest value, receipt age, configured line,
        /// data quality and last event — and why any action is unavailable.
        /// Rewritten on a tick; the focused row's name is not.
        /// </summary>
        public string DetailText(AlarmSnapshot s)
        {
            var parts = new List<string> { AlarmPhrasing.Status(s) };
            if (s.LastEvent != null)
                parts.Add(Lexicon.Get("alarms.status.last_event", ("event", AlarmPhrasing.EventInWords(s.LastEvent))));
            if (!CanAcknowledge(s) && !CanSnooze(s) && !CanResume(s))
                parts.Add(Lexicon.Get("alarms.nothing_active", ("alarm", s.Definition.Name)));
            if (s.Definition.Condition == AlarmCondition.RiseFromBaseline && !CanCaptureBaseline(s))
                parts.Add(Lexicon.Get("alarms.baseline_refused", ("reason",
                    s.Transmitting ? Lexicon.Get("alarms.baseline_refused.in_transmit") : Lexicon.Get("alarms.baseline_refused.no_fresh_sample"))));
            return string.Join(" ", parts);
        }

        // ── explicit reads, at every verbosity ──

        public string ReadStatus(AlarmSnapshot s) => AlarmPhrasing.Status(s);

        public string ReadActive() =>
            AlarmPhrasing.ActiveSummary(_service.Snapshot(), _service.IsConnected, _service.StoreState, _service.StoreProblem);

        // ── which actions apply ──

        public static bool CanAcknowledge(AlarmSnapshot s) =>
            HasEpisode(s) && s.Notification != AlarmNotificationState.Acknowledged;

        public static bool CanSnooze(AlarmSnapshot s) => HasEpisode(s);

        public static bool CanResume(AlarmSnapshot s) =>
            HasEpisode(s) && s.Notification is AlarmNotificationState.Acknowledged or AlarmNotificationState.Snoozed;

        public static bool CanCaptureBaseline(AlarmSnapshot s) =>
            s.Definition.Condition == AlarmCondition.RiseFromBaseline
            && !s.Transmitting
            && s.LastFresh.HasValue
            && s.Data is AlarmDataState.Fresh or AlarmDataState.Recovering
            && s.LastFreshAgeSeconds <= s.Definition.FreshnessAllowanceSeconds;

        private static bool HasEpisode(AlarmSnapshot s) =>
            s.Condition is AlarmConditionState.Active or AlarmConditionState.LastKnownActive;

        // ── actions, each returning the receipt to speak ──

        public string Acknowledge(string alarmId)
        {
            AlarmSnapshot? s = _service.SnapshotOf(alarmId);
            if (s == null) return Lexicon.Get("alarms.dialog.select_one");
            foreach (AlarmEvent e in _service.Acknowledge(alarmId))
                if (e.Kind == AlarmEventKind.Acknowledged) return Lexicon.Get("alarms.acknowledged", ("alarm", s.Definition.Name));
            return Lexicon.Get("alarms.nothing_active", ("alarm", s.Definition.Name));
        }

        public string Snooze(string alarmId, double seconds)
        {
            AlarmSnapshot? s = _service.SnapshotOf(alarmId);
            if (s == null) return Lexicon.Get("alarms.dialog.select_one");
            foreach (AlarmEvent e in _service.Snooze(alarmId, seconds))
                if (e.Kind == AlarmEventKind.Snoozed)
                    return Lexicon.Get("alarms.snoozed", ("seconds", AlarmPhrasing.Age(seconds)), ("alarm", s.Definition.Name));
            return Lexicon.Get("alarms.nothing_active", ("alarm", s.Definition.Name));
        }

        public string Resume(string alarmId)
        {
            AlarmSnapshot? s = _service.SnapshotOf(alarmId);
            if (s == null) return Lexicon.Get("alarms.dialog.select_one");
            foreach (AlarmEvent e in _service.Resume(alarmId))
                if (e.Kind == AlarmEventKind.Resumed) return Lexicon.Get("alarms.resumed", ("alarm", s.Definition.Name));
            return Lexicon.Get("alarms.nothing_active", ("alarm", s.Definition.Name));
        }

        public string CaptureBaseline(string alarmId)
        {
            AlarmSnapshot? s = _service.SnapshotOf(alarmId);
            if (s == null) return Lexicon.Get("alarms.dialog.select_one");
            MeterUnits units = s.Definition.Selector.Units;
            foreach (AlarmEvent e in _service.CaptureBaseline(alarmId))
            {
                if (e.Kind == AlarmEventKind.BaselineCaptured)
                    return AlarmPhrasing.Tidy(Lexicon.Get("alarms.baseline_captured",
                        ("value", AlarmPhrasing.Value(e.Baseline, units)), ("units", MeterReading.UnitsText(units))));
                if (e.Kind == AlarmEventKind.BaselineRefused)
                    return Lexicon.Get("alarms.baseline_refused", ("reason", AlarmPhrasing.BaselineRefusal(e.Detail)));
            }
            return Lexicon.Get("alarms.baseline_refused", ("reason", Lexicon.Get("alarms.baseline_refused.not_applicable")));
        }

        public string SetEnabled(string alarmId, bool enabled)
        {
            AlarmSnapshot? s = _service.SnapshotOf(alarmId);
            if (s == null) return Lexicon.Get("alarms.dialog.select_one");
            if (!_service.SetEnabled(alarmId, enabled)) return Lexicon.Get("alarms.dialog.save_failed");
            return Lexicon.Get(enabled ? "alarms.dialog.enabled" : "alarms.dialog.disabled", ("alarm", s.Definition.Name));
        }

        /// <summary>
        /// Delete is reachable from an OPEN EDITOR, never from a selected row.
        /// Ruled by Noel 2026-09-22 (#566): <i>"move it in."</i> The main
        /// dialog is the list, Add, Edit and the status actions.
        /// </summary>
        /// <remarks>
        /// An Add form has nothing to delete yet, so the button is offered only
        /// over an alarm that is already saved.
        /// </remarks>
        public static bool CanDelete(AlarmEditorModel editor) => !editor.IsNew;

        /// <summary>The confirm for the alarm the editor is open on, by name, saying there is no undo.</summary>
        public string DeleteConfirmation(AlarmEditorModel editor) =>
            Lexicon.Get("alarms.dialog.delete_confirm", ("alarm", SavedName(editor)));

        /// <summary>Delete the alarm the editor is open on, and return the one receipt to speak.</summary>
        public string Delete(AlarmEditorModel editor)
        {
            if (!CanDelete(editor)) return Lexicon.Get("alarms.dialog.delete_failed");
            string name = SavedName(editor);
            return _service.Remove(editor.Id)
                ? Lexicon.Get("alarms.dialog.deleted", ("alarm", name))
                : Lexicon.Get("alarms.dialog.delete_failed");
        }

        /// <summary>
        /// The name the alarm is SAVED under. An unsaved edit to the name field
        /// must not rename the thing the operator is being asked to confirm.
        /// </summary>
        private string SavedName(AlarmEditorModel editor) =>
            _service.SnapshotOf(editor.Id)?.Definition.Name ?? editor.Name;

        public AlarmEvent? Preview(string alarmId) => _service.Preview(alarmId);

        // ── history ──

        public IReadOnlyList<AlarmEvent> History() => _service.RecentEvents();

        public string HistoryRow(AlarmEvent e)
        {
            string when = _service.UtcAt(e.AtMs).ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            return Lexicon.Get("alarms.dialog.history_row", ("when", when), ("event", AlarmPhrasing.EventInWords(e)));
        }

        // ── the recorded set ──

        /// <summary>Every published meter, with whether an alarm already records it and whether the operator added it.</summary>
        public IReadOnlyList<(MeterDescriptor Meter, bool ByAlarm, bool ByOperator)> RecordedChoices()
        {
            var byAlarm = new HashSet<int>();
            foreach (AlarmSnapshot s in _service.Snapshot())
                if (s.ResolvedMeter != null) byAlarm.Add(s.ResolvedMeter.Index);
            var byOperator = new HashSet<int>();
            IReadOnlyList<MeterDescriptor> inventory = _service.Inventory;
            foreach (MeterSelector sel in _service.RecordOnlyMeters)
            {
                MeterSelectorResolution r = sel.Resolve(inventory);
                if (r.IsResolved) byOperator.Add(r.Match!.Index);
            }
            var list = new List<(MeterDescriptor, bool, bool)>();
            foreach (MeterDescriptor d in inventory)
                list.Add((d, byAlarm.Contains(d.Index), byOperator.Contains(d.Index)));
            return list;
        }

        public bool SetRecorded(MeterDescriptor meter, bool record) =>
            _service.SetRecordOnly(MeterSelector.From(meter), record);

        // ── the editor ──

        public AlarmEditorModel NewEditor() => new AlarmEditorModel(_service, null);

        public AlarmEditorModel EditorFor(AlarmSnapshot s) => new AlarmEditorModel(_service, s.Definition);

        /// <summary>Shipped presets for this connection's meters, available or not with the reason, then the operator's own.</summary>
        public IReadOnlyList<PresetChoice> PresetChoices()
        {
            var list = new List<PresetChoice> { new PresetChoice(Lexicon.Get("alarms.editor.preset_none"), PresetChoiceKind.None, null, "") };
            foreach (AlarmPresetOffer o in AlarmPresets.Offer(_service.Inventory, _service.RadioSerial, _newId))
            {
                string name = o.Definition?.Name ?? AlarmPresets.PresetName(o.Key, o.Meter);
                string label = name + " (" + Lexicon.Get("alarms.preset.shipped") + ")";
                if (!o.IsAvailable) label += " — " + Lexicon.Get(o.Problem);
                list.Add(new PresetChoice(label, PresetChoiceKind.Shipped, o.Definition, o.IsAvailable ? "" : Lexicon.Get(o.Problem)));
            }
            if (_presets != null)
            {
                foreach (OperatorPreset p in _presets.Load(out _))
                {
                    AlarmDefinition applied = OperatorPresetStore.Apply(p, _service.RadioSerial, _service.Inventory, out MeterSelectorResolution r);
                    string label = p.Name + " (" + Lexicon.Get("alarms.preset.yours") + ")";
                    string problem = r.IsResolved ? "" : AlarmPhrasing.ResolutionWords(r.Status);
                    if (problem.Length != 0) label += " — " + problem;
                    list.Add(new PresetChoice(label, PresetChoiceKind.Operator, applied, problem, p));
                }
            }
            return list;
        }

        /// <summary>Save the editor's definition as the operator's own preset, under their name.</summary>
        public string SaveAsPreset(AlarmEditorModel editor, string name)
        {
            if (_presets == null) return Lexicon.Get("alarms.editor.preset_save_failed");
            IReadOnlyList<AlarmValidationProblem> problems = editor.Validate();
            if (problems.Count != 0) return Lexicon.Get(problems[0].LexiconKey);
            return _presets.Save(name, editor.Build())
                ? Lexicon.Get("alarms.editor.preset_saved", ("name", name.Trim()))
                : Lexicon.Get("alarms.editor.preset_save_failed");
        }

        /// <summary>Save the editor's definition: add or update. Returns the receipt, and whether it was saved.</summary>
        public string Save(AlarmEditorModel editor, out bool saved)
        {
            saved = false;
            IReadOnlyList<AlarmValidationProblem> problems = editor.Validate();
            if (problems.Count != 0) return Lexicon.Get(problems[0].LexiconKey);
            AlarmDefinition def = editor.Build();
            saved = editor.IsNew ? _service.Add(def) : _service.Update(def);
            return saved ? Lexicon.Get("alarms.dialog.saved", ("alarm", def.Name)) : Lexicon.Get("alarms.dialog.save_failed");
        }
    }

    /// <summary>
    /// The Add and Edit form's fields, in the design's order, with validation
    /// that names the field, a preset that fills every field but leaves it
    /// inspectable, and a summary in words.
    /// </summary>
    public sealed class AlarmEditorModel
    {
        private readonly AlarmService _service;

        internal AlarmEditorModel(AlarmService service, AlarmDefinition? existing)
        {
            _service = service;
            IsNew = existing == null;
            AlarmDefinition d = existing ?? AlarmDefinition.NewLevel(Guid.NewGuid().ToString("N"), "", service.RadioSerial,
                new MeterSelector("", "", 0, MeterUnits.None, ""), AlarmDirection.AtOrAbove, 0, 0);
            Load(d);
        }

        public bool IsNew { get; }
        public string Id { get; private set; } = "";
        public int Revision { get; private set; }
        public string RadioSerial => _service.RadioSerial;

        public string Name { get; set; } = "";
        public MeterSelector? Selector { get; private set; }
        public AlarmCondition Condition { get; set; }
        public AlarmDirection Direction { get; set; }
        public double Threshold { get; set; }
        public double Hysteresis { get; set; }
        public AlarmPersistence Persistence { get; set; }
        public int SustainedCount { get; set; }
        public double SustainedSeconds { get; set; }
        public AlarmScope Scope { get; set; }
        public double FreshnessAllowanceSeconds { get; set; }
        public double ReminderIntervalSeconds { get; set; }
        public double WorseningStep { get; set; }
        public AlarmActionClass Action { get; set; }
        public bool Enabled { get; set; }
        public bool IsTest { get; set; }
        public string PresetKey { get; private set; } = "";
        public double? SentinelValue { get; set; }
        public double TrendWindowSeconds { get; set; }
        public double TrendBandSeconds { get; set; }
        public int TrendMinSamplesPerBand { get; set; }
        public double TrendIntervalMinSeconds { get; set; }
        public double TrendIntervalMaxSeconds { get; set; }
        public double TrendResetRise { get; set; }
        public int ClearSamples { get; set; }
        public double ClearSeconds { get; set; }

        /// <summary>Units in words for the accessible names of the numeric fields, or empty until a meter is chosen.</summary>
        public string UnitsText => Selector == null ? "" : Selector.UnitsText;

        private void Load(AlarmDefinition d)
        {
            Id = d.Id;
            Revision = d.Revision;
            Name = d.Name;
            Selector = string.IsNullOrEmpty(d.Selector.Name) ? null : d.Selector;
            Condition = d.Condition;
            Direction = d.Direction;
            Threshold = d.Threshold;
            Hysteresis = d.Hysteresis;
            Persistence = d.Persistence;
            SustainedCount = d.SustainedCount;
            SustainedSeconds = d.SustainedSeconds;
            Scope = d.Scope;
            FreshnessAllowanceSeconds = d.FreshnessAllowanceSeconds;
            ReminderIntervalSeconds = d.ReminderIntervalSeconds;
            WorseningStep = d.WorseningStep;
            Action = d.Action;
            Enabled = d.Enabled;
            IsTest = d.IsTest;
            PresetKey = d.PresetKey;
            SentinelValue = d.SentinelValue;
            TrendWindowSeconds = d.TrendWindowSeconds;
            TrendBandSeconds = d.TrendBandSeconds;
            TrendMinSamplesPerBand = d.TrendMinSamplesPerBand;
            TrendIntervalMinSeconds = d.TrendIntervalMinSeconds;
            TrendIntervalMaxSeconds = d.TrendIntervalMaxSeconds;
            TrendResetRise = d.TrendResetRise;
            ClearSamples = d.ClearSamples;
            ClearSeconds = d.ClearSeconds;
        }

        /// <summary>A preset fills every field and leaves them inspectable. The id and the new-or-existing state are kept.</summary>
        public void ApplyPreset(PresetChoice choice)
        {
            if (choice.Definition == null) return;
            string id = Id;
            int revision = Revision;
            Load(choice.Definition);
            Id = id;
            Revision = revision;
            if (choice.Kind == PresetChoiceKind.Operator) PresetKey = "";
        }

        /// <summary>
        /// The searchable meter list for this connection. Duplicate names carry
        /// their count and session index so the operator can tell copies apart;
        /// availability says whether the meter has reported yet.
        /// </summary>
        public IReadOnlyList<MeterChoice> MeterChoices(string search)
        {
            IReadOnlyList<MeterDescriptor> inventory = _service.Inventory;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (MeterDescriptor d in inventory)
                counts[d.Name] = counts.TryGetValue(d.Name, out int n) ? n + 1 : 1;

            string q = (search ?? "").Trim();
            var list = new List<MeterChoice>();
            foreach (MeterDescriptor d in inventory)
            {
                if (q.Length != 0
                    && d.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && d.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && d.SourceLabel.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                int dup = counts[d.Name];
                string availability = _service.HasReported(d.Index)
                    ? Lexicon.Get("alarms.editor.meter_available")
                    : Lexicon.Get("alarms.editor.meter_silent");
                if (dup > 1)
                    availability += ", " + Lexicon.Get("alarms.editor.meter_duplicate",
                        ("count", dup.ToString(CultureInfo.CurrentCulture)), ("index", d.Index.ToString(CultureInfo.CurrentCulture)));
                string label = AlarmPhrasing.Tidy(Lexicon.Get("alarms.editor.meter_row",
                    ("name", d.Name), ("description", d.Description), ("source", d.SourceLabel),
                    ("units", d.UnitsText), ("availability", availability)));
                list.Add(new MeterChoice(d, label, dup));
            }
            return list;
        }

        public bool HasInventory => _service.Inventory.Count > 0;

        public void ChooseMeter(MeterDescriptor meter) => Selector = MeterSelector.From(meter);

        public AlarmDefinition Build() => new AlarmDefinition
        {
            Id = Id,
            Revision = Revision,
            Name = (Name ?? "").Trim(),
            Enabled = Enabled,
            RadioSerial = RadioSerial,
            Selector = Selector ?? new MeterSelector("", "", 0, MeterUnits.None, ""),
            Condition = Condition,
            Direction = Direction,
            Threshold = Threshold,
            Hysteresis = Hysteresis,
            Persistence = Persistence,
            SustainedCount = SustainedCount,
            SustainedSeconds = SustainedSeconds,
            Scope = Scope,
            FreshnessAllowanceSeconds = FreshnessAllowanceSeconds,
            ReminderIntervalSeconds = ReminderIntervalSeconds,
            WorseningStep = WorseningStep,
            Action = Action,
            IsTest = IsTest,
            PresetKey = PresetKey,
            SentinelValue = SentinelValue,
            TrendWindowSeconds = TrendWindowSeconds,
            TrendBandSeconds = TrendBandSeconds,
            TrendMinSamplesPerBand = TrendMinSamplesPerBand,
            TrendIntervalMinSeconds = TrendIntervalMinSeconds,
            TrendIntervalMaxSeconds = TrendIntervalMaxSeconds,
            TrendResetRise = TrendResetRise,
            ClearSamples = ClearSamples,
            ClearSeconds = ClearSeconds,
        };

        /// <summary>Every problem, by field, in the form's order — so the first one is where focus should go.</summary>
        public IReadOnlyList<AlarmValidationProblem> Validate() => Build().Validate();

        /// <summary>The out-of-range review message, or null. Not a problem: it may be intentional.</summary>
        public string? RangeReview()
        {
            if (Selector == null || Condition != AlarmCondition.Level) return null;
            MeterSelectorResolution r = Selector.Resolve(_service.Inventory);
            if (!r.IsResolved) return null;
            MeterDescriptor m = r.Match!;
            if (Threshold >= m.Low && Threshold <= m.High) return null;
            return Lexicon.Get("alarms.validation.out_of_range_review",
                ("low", AlarmPhrasing.Value(m.Low, m.Units)), ("high", AlarmPhrasing.Value(m.High, m.Units)), ("units", m.UnitsText));
        }

        /// <summary>The read-only summary in words, including the journal's disclosed cost.</summary>
        public string Summary()
        {
            AlarmDefinition d = Build();
            string persistence = d.Persistence == AlarmPersistence.FirstFreshSample
                ? Lexicon.Get("alarms.editor.persistence_first")
                : Lexicon.Get("alarms.editor.persistence_sustained");
            string scope = Lexicon.Get(d.Scope == AlarmScope.TransmitOnly ? "alarms.editor.scope_transmit" : "alarms.editor.scope_always");
            // Arithmetic, not a benchmark: about 450 bytes a record at the slow
            // meters' half-a-callback-a-second.
            const double bytesPerRecord = 450, callbacksPerSecond = 0.5;
            double mbPerHour = bytesPerRecord * callbacksPerSecond * 3600 / (1024 * 1024);
            return AlarmPhrasing.Tidy(Lexicon.Get("alarms.editor.summary_text",
                ("meter", Selector?.Label ?? ""),
                ("boundary", AlarmPhrasing.Boundary(d)),
                ("persistence", persistence.ToLowerInvariant()),
                ("scope", scope.ToLowerInvariant()),
                ("clear", d.ClearSamples.ToString(CultureInfo.CurrentCulture)),
                ("reminder", AlarmPhrasing.Age(d.ReminderIntervalSeconds)),
                ("cost", mbPerHour.ToString("0.0", CultureInfo.CurrentCulture) + " MB"),
                ("name", d.Name.Length == 0 ? Lexicon.Get("alarms.editor.name") : d.Name)));
        }

        /// <summary>Parse a number the way the operator typed it, in their locale. False leaves the field as it was.</summary>
        public static bool TryParse(string? text, out double value) =>
            double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
