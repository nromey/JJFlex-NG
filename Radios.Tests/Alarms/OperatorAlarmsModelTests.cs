using System;
using System.IO;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// The dialog's decisions, tested with no window: rows in words, the
    /// detail field, the actions and their receipts, the editor's fields and
    /// validation, the preset picker, and Save as preset. DeskGuard refuses
    /// the window on a live desk and JJFLEX_TIER1_DESK_FREE is never set.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class OperatorAlarmsModelTests : IDisposable
    {
        private const string Serial = "1234-5678-9012-3456";
        private static readonly MeterDescriptor Pa = PaTemperatureReplayFixture.Meter;
        private static readonly MeterDescriptor SupplyA = new MeterDescriptor(2, "+13.8A", "+13.8V at PA", "RAD", 2, MeterUnits.Volts, 10.5, 15);
        private static readonly MeterDescriptor Fwd = new MeterDescriptor(1, "FWDPWR", "RF Power Forward", "TX-", 4, MeterUnits.Dbm, -30, 50);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-alarmmodel-" + Guid.NewGuid().ToString("N"));
        private readonly FakeAlarmFeed _feed = new();
        private readonly ManualAlarmClock _clock = new() { NowMs = 10_000 };
        private AlarmService? _service;

        public void Dispose()
        {
            _service?.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private OperatorAlarmsModel Up(bool connect = true)
        {
            _service = new AlarmService(_feed, new AlarmDefinitionStore(_root), _clock, null, startWatchdog: false);
            if (connect) _feed.Connect(Serial, Pa, SupplyA, Fwd);
            return new OperatorAlarmsModel(_service, new OperatorPresetStore(_root), () => Guid.NewGuid().ToString("N"));
        }

        private void Deliver(MeterDescriptor m, float v)
        {
            _clock.Advance(2000);
            _feed.Deliver(m, v);
        }

        [Fact]
        public void With_no_radio_the_status_says_so_and_there_are_no_rows()
        {
            var model = Up(connect: false);
            Assert.Empty(model.Rows());
            Assert.Equal("No radio is connected. Alarms are saved per radio, so connect first.", model.StatusText());
            Assert.Equal("No radio is connected, so nothing is being watched.", model.ReadActive());
        }

        [Fact]
        public void A_preset_applied_from_the_picker_fills_every_field_and_saves_as_a_new_alarm()
        {
            var model = Up();
            Assert.Equal("No alarms yet. Add one, or start from a preset.", model.StatusText());

            var choices = model.PresetChoices();
            Assert.Equal("No preset, define it yourself", choices[0].Label);
            var pa = choices.Single(c => c.Kind == PresetChoiceKind.Shipped && c.Definition?.PresetKey == AlarmPresets.PaTemperature);
            Assert.Equal("PA temperature high (shipped preset)", pa.Label);
            Assert.Equal(3 + 3, choices.Count(c => c.Kind == PresetChoiceKind.Shipped));   // one supply meter here

            AlarmEditorModel editor = model.NewEditor();
            Assert.True(editor.IsNew);
            editor.ApplyPreset(pa);
            Assert.Equal("PA temperature high", editor.Name);
            Assert.Equal(60, editor.Threshold);
            Assert.Equal("degrees C", editor.UnitsText);
            Assert.False(editor.Enabled);
            Assert.Empty(editor.Validate());
            Assert.Null(editor.RangeReview());

            string receipt = model.Save(editor, out bool saved);
            Assert.True(saved);
            Assert.Equal("PA temperature high saved.", receipt);
            var row = Assert.Single(model.Rows());
            Assert.Equal("PA temperature high. PATEMP (PA Temperature). at or above 60 degrees C. disabled", model.RowText(row));
        }

        [Fact]
        public void Validation_names_the_first_offending_field_and_keeps_the_entered_values()
        {
            var model = Up();
            AlarmEditorModel editor = model.NewEditor();
            editor.Name = "Mine";
            editor.ChooseMeter(Pa);
            editor.Threshold = double.NaN;
            editor.Hysteresis = -1;
            var problems = editor.Validate();
            Assert.Equal("threshold", problems[0].Field);
            Assert.Equal("The line must be a number.", Lexicon.Get(problems[0].LexiconKey));
            Assert.Equal("hysteresis", problems[1].Field);
            Assert.Equal(-1, editor.Hysteresis);   // retained

            string receipt = model.Save(editor, out bool saved);
            Assert.False(saved);
            Assert.Equal("The line must be a number.", receipt);
        }

        [Fact]
        public void An_unusual_line_outside_the_published_range_is_permitted_with_a_review_message()
        {
            var model = Up();
            AlarmEditorModel editor = model.NewEditor();
            editor.Name = "Way up";
            editor.ChooseMeter(Pa);
            editor.Threshold = 150;
            Assert.Empty(editor.Validate());
            Assert.Equal("The line is outside this meter's published range of 0 to 120 degrees C. Save it if you mean it.", editor.RangeReview());
        }

        [Fact]
        public void The_meter_list_is_searchable_and_says_reporting_duplicates_and_units()
        {
            var model = Up();
            Deliver(Pa, 30f);
            AlarmEditorModel editor = model.NewEditor();
            var all = editor.MeterChoices("");
            Assert.Equal(3, all.Count);
            var pa = all.Single(c => c.Meter.Name == "PATEMP");
            Assert.Equal("PATEMP, PA Temperature, TX-:4, degrees C, reporting", pa.Label);
            var fwd = all.Single(c => c.Meter.Name == "FWDPWR");
            Assert.EndsWith("dBm, no reading yet", fwd.Label);
            Assert.Single(editor.MeterChoices("13.8"));
            Assert.Single(editor.MeterChoices("pa temp"));   // description, case-insensitive
        }

        [Fact]
        public void Duplicate_meters_carry_their_count_and_session_index()
        {
            _service = new AlarmService(_feed, new AlarmDefinitionStore(_root), _clock, null, startWatchdog: false);
            _feed.Connect(Serial, Pa, Pa with { Index = 40 });
            var model = new OperatorAlarmsModel(_service, null);
            var choices = model.NewEditor().MeterChoices("PATEMP");
            Assert.Equal(2, choices.Count);
            Assert.EndsWith("one of 2 with this name, session index 11", choices[0].Label);
            Assert.EndsWith("one of 2 with this name, session index 40", choices[1].Label);
        }

        [Fact]
        public void The_editor_summary_reads_as_one_sentence_with_the_disclosed_journal_cost()
        {
            var model = Up();
            AlarmEditorModel editor = model.NewEditor();
            editor.ApplyPreset(model.PresetChoices().Single(c => c.Definition?.PresetKey == AlarmPresets.PaTemperature));
            Assert.Equal("PA temperature high watches PATEMP, TX-:4 and fires when at or above 60 degrees C, on the first fresh reading at the line, "
                + "whenever connected. Cleared after 2 readings past the margin. Repeats every 30 seconds while active. "
                + "While enabled, every reading of PATEMP, TX-:4 is recorded on this computer, about 0.8 MB per hour.", editor.Summary());
        }

        [Fact]
        public void Actions_apply_only_to_an_active_episode_and_return_their_receipts()
        {
            var model = Up();
            AlarmEditorModel editor = model.NewEditor();
            editor.ApplyPreset(model.PresetChoices().Single(c => c.Definition?.PresetKey == AlarmPresets.PaTemperature));
            editor.Enabled = true;
            model.Save(editor, out _);
            string id = model.Rows()[0].Definition.Id;

            Assert.False(OperatorAlarmsModel.CanAcknowledge(model.Rows()[0]));
            Assert.Equal("PA temperature high has no active warning to act on.", model.Acknowledge(id));

            Deliver(Pa, 61f);
            var row = model.Rows()[0];
            Assert.True(OperatorAlarmsModel.CanAcknowledge(row));
            Assert.True(OperatorAlarmsModel.CanSnooze(row));
            Assert.False(OperatorAlarmsModel.CanResume(row));
            Assert.Equal("PA temperature high acknowledged. It is still active; reminders stop until it clears.", model.Acknowledge(id));
            Assert.True(OperatorAlarmsModel.CanResume(model.Rows()[0]));
            Assert.Equal("PA temperature high snoozed for 30 seconds.", model.Snooze(id, 30));
            Assert.Equal("PA temperature high reminders resumed.", model.Resume(id));
            Assert.Equal("PA temperature high. PATEMP (PA Temperature). at or above 60 degrees C. active", model.RowText(model.Rows()[0]));

            string detail = model.DetailText(model.Rows()[0]);
            Assert.StartsWith("PA temperature high: enabled, watching, active. not acknowledged. Latest reading 61 degrees C,", detail);
            Assert.Contains("Last event:", detail);
        }

        [Fact]
        public void Capture_baseline_is_offered_only_for_a_baseline_alarm_in_receive_with_a_fresh_reading()
        {
            var model = Up();
            AlarmEditorModel editor = model.NewEditor();
            editor.ApplyPreset(model.PresetChoices().Single(c => c.Definition?.PresetKey == AlarmPresets.PaRiseFromBaseline));
            editor.Enabled = true;
            model.Save(editor, out _);
            string id = model.Rows()[0].Definition.Id;
            Assert.False(OperatorAlarmsModel.CanCaptureBaseline(model.Rows()[0]));
            Assert.Equal("The baseline was not captured: there is no fresh reading to take it from.", model.CaptureBaseline(id));

            Deliver(Pa, 25.7f);
            Assert.True(OperatorAlarmsModel.CanCaptureBaseline(model.Rows()[0]));
            _feed.Key(true);
            Assert.False(OperatorAlarmsModel.CanCaptureBaseline(model.Rows()[0]));
            Assert.Equal("The baseline was not captured: you are transmitting; capture it in receive.", model.CaptureBaseline(id));
            _feed.Key(false);
            Assert.Equal("Baseline captured: 25.7 degrees C.", model.CaptureBaseline(id));
        }

        [Fact]
        public void Enable_and_disable_speak_receipts_and_persist()
        {
            var model = Up();
            AlarmEditorModel editor = model.NewEditor();
            editor.ApplyPreset(model.PresetChoices().Single(c => c.Definition?.PresetKey == AlarmPresets.PaTemperature));
            model.Save(editor, out _);
            string id = model.Rows()[0].Definition.Id;
            Assert.Equal("PA temperature high enabled. It fires on the first fresh reading at the line, so it may fire straight away.", model.SetEnabled(id, true));
            Assert.True(model.Rows()[0].Definition.Enabled);
            Assert.Equal("PA temperature high disabled. Its definition is kept.", model.SetEnabled(id, false));
        }

        /// <summary>
        /// Ruled by Noel 2026-09-22 (#566): <i>"move it in."</i> Delete is a
        /// button inside the Edit form, not beside Edit on the main dialog, so
        /// the model offers it over an open editor and nowhere else.
        /// </summary>
        [Fact]
        public void Delete_is_reachable_from_an_open_editor_and_not_from_a_selected_row()
        {
            var model = Up();
            AlarmEditorModel add = model.NewEditor();
            add.ApplyPreset(model.PresetChoices().Single(c => c.Definition?.PresetKey == AlarmPresets.PaTemperature));
            model.Save(add, out _);

            // An Add form has nothing to delete yet.
            Assert.False(OperatorAlarmsModel.CanDelete(model.NewEditor()));

            AlarmEditorModel editor = model.EditorFor(model.Rows()[0]);
            Assert.True(OperatorAlarmsModel.CanDelete(editor));
            Assert.Equal("Delete the alarm PA temperature high? There is no undo.", model.DeleteConfirmation(editor));

            // An unsaved edit to the name field does not rename the thing the
            // operator is being asked to confirm.
            editor.Name = "Something else entirely";
            Assert.Equal("Delete the alarm PA temperature high? There is no undo.", model.DeleteConfirmation(editor));

            Assert.Equal("PA temperature high deleted.", model.Delete(editor));
            Assert.Empty(model.Rows());

            // A second press on a gone alarm does not claim a second success.
            Assert.Equal("The alarm could not be deleted.", model.Delete(editor));
        }

        [Fact]
        public void Editing_keeps_the_id_makes_a_new_revision_and_cancel_is_simply_not_saving()
        {
            var model = Up();
            AlarmEditorModel add = model.NewEditor();
            add.ApplyPreset(model.PresetChoices().Single(c => c.Definition?.PresetKey == AlarmPresets.PaTemperature));
            model.Save(add, out _);
            var before = model.Rows()[0];

            AlarmEditorModel edit = model.EditorFor(before);
            Assert.False(edit.IsNew);
            edit.Threshold = 55;
            // cancelled: nothing saved
            Assert.Equal(60, model.Rows()[0].Definition.Threshold);

            AlarmEditorModel edit2 = model.EditorFor(model.Rows()[0]);
            edit2.Threshold = 55;
            model.Save(edit2, out bool saved);
            Assert.True(saved);
            Assert.Equal(before.Definition.Id, model.Rows()[0].Definition.Id);
            Assert.Equal(2, model.Rows()[0].Definition.Revision);
            Assert.Equal(55, model.Rows()[0].Definition.Threshold);
        }

        [Fact]
        public void Save_as_preset_keeps_it_on_this_machine_and_offers_it_for_another_radio_with_identity_rediscovered()
        {
            var model = Up();
            AlarmEditorModel editor = model.NewEditor();
            editor.Name = "My PA line";
            editor.ChooseMeter(Pa);
            editor.Threshold = 55;
            editor.Hysteresis = 2;
            editor.Enabled = true;
            Assert.Equal("Preset My PA line saved. It is kept on this computer and offered for any radio.", model.SaveAsPreset(editor, "My PA line"));

            // Another radio, where PATEMP sits at a different index.
            _feed.Disconnect();
            _feed.Connect("6300-0000-0000-0000", Pa with { Index = 9 }, Fwd);
            var mine = model.PresetChoices().Single(c => c.Kind == PresetChoiceKind.Operator);
            Assert.Equal("My PA line (your preset)", mine.Label);
            Assert.True(mine.IsAvailable);
            Assert.Equal("6300-0000-0000-0000", mine.Definition!.RadioSerial);
            Assert.False(mine.Definition.Enabled);   // intent is not carried between radios
            Assert.Equal(55, mine.Definition.Threshold);
            Assert.Equal("", mine.Definition.PresetKey);

            // A radio without the meter: offered, marked, and the meter must be chosen.
            _feed.Disconnect();
            _feed.Connect("8400-0000-0000-0000", Fwd);
            var missing = model.PresetChoices().Single(c => c.Kind == PresetChoiceKind.Operator);
            Assert.Equal("My PA line (your preset) — not published by this radio", missing.Label);
            AlarmEditorModel apply = model.NewEditor();
            apply.ApplyPreset(missing);
            Assert.Equal("PATEMP", apply.Selector!.Name);
            Assert.Equal(55, apply.Threshold);
        }

        [Fact]
        public void The_recorded_meter_choices_show_alarm_watched_meters_and_the_operator_extras()
        {
            var model = Up();
            AlarmEditorModel editor = model.NewEditor();
            editor.ApplyPreset(model.PresetChoices().Single(c => c.Definition?.PresetKey == AlarmPresets.PaTemperature));
            model.Save(editor, out _);
            Assert.True(model.SetRecorded(Fwd, true));
            var choices = model.RecordedChoices();
            Assert.True(choices.Single(c => c.Meter.Name == "PATEMP").ByAlarm);
            Assert.True(choices.Single(c => c.Meter.Name == "FWDPWR").ByOperator);
            Assert.False(choices.Single(c => c.Meter.Name == "+13.8A").ByAlarm || choices.Single(c => c.Meter.Name == "+13.8A").ByOperator);
        }

        [Fact]
        public void History_rows_carry_a_clock_time_and_words_and_the_preview_is_labelled()
        {
            var model = Up();
            AlarmEditorModel editor = model.NewEditor();
            editor.ApplyPreset(model.PresetChoices().Single(c => c.Definition?.PresetKey == AlarmPresets.PaTemperature));
            model.Save(editor, out _);
            string id = model.Rows()[0].Definition.Id;
            model.Preview(id);
            var rows = model.History();
            Assert.Contains(rows, e => e.Detail == "preview");
            string row = model.HistoryRow(rows.Last());
            Assert.Matches(@"^\d\d:\d\d:\d\d: Test warning previewed\.$", row);
        }

        [Fact]
        public void Numbers_parse_in_the_operator_locale_or_invariant()
        {
            Assert.True(AlarmEditorModel.TryParse("12.5", out double v));
            Assert.Equal(12.5, v);
            Assert.True(AlarmEditorModel.TryParse(" -3 ", out v));
            Assert.Equal(-3, v);
            Assert.False(AlarmEditorModel.TryParse("sixty", out _));
        }
    }
}
