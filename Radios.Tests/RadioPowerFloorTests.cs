using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Radios;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// A radio that cannot run at zero watts (#859). Measured on the FLEX-6300, so
/// the rule names that model; the FLEX-8600 records at zero and every other
/// model is unmeasured, so nothing else may move. Test your microphone is the
/// one deliberate exception, and it is pinned here so that nothing removes it
/// by accident.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class RadioPowerFloorTests
{
    private const string Six = "FLEX-6300";
    private const string Eight = "FLEX-8600";

    private static AudioCheckPowerStep Plan(string model, AudioCheckPowerModes mode, int current, bool dummy,
        AudioCheckListenMethods method = AudioCheckListenMethods.RecordPlayback, int low = 10) =>
        AudioCheckPowerRule.Plan(model, method, mode, low, current, dummy);

    private static AudioCheckPowerStep Sets(int watts) => new(AudioCheckPowerAct.SetPower, watts);
    private static readonly AudioCheckPowerStep Leaves = new(AudioCheckPowerAct.None);
    private static readonly AudioCheckPowerStep Engages = new(AudioCheckPowerAct.EngageDummyLoad);

    // ── Dummy load, on a 6300 ───────────────────────────────────────────

    [Theory]
    [InlineData(100, 1)]    // lowers: the check runs AT a watt
    [InlineData(2, 1)]
    [InlineData(0, 1)]      // raises: a radio at zero
    public void The_dummy_load_choice_runs_at_one_watt_on_a_6300(int current, int expected) =>
        Assert.Equal(Sets(expected), Plan(Six, AudioCheckPowerModes.DummyLoad, current, false));

    [Fact]
    public void A_6300_already_at_one_watt_needs_nothing_changed() =>
        Assert.Equal(Leaves, Plan(Six, AudioCheckPowerModes.DummyLoad, 1, false));

    [Theory]
    [InlineData(AudioCheckListenMethods.Monitor)]
    [InlineData(AudioCheckListenMethods.RecordPlayback)]
    public void Both_methods_that_obey_the_choice_take_the_floor(AudioCheckListenMethods method) =>
        Assert.Equal(Sets(1), Plan(Six, AudioCheckPowerModes.DummyLoad, 50, false, method));

    // ── The one deliberate exception (Noel, 2026-11-01 08:08) ───────────

    [Fact]
    public void Test_your_microphone_stays_at_zero_on_a_6300()
    {
        Assert.Equal(Engages, Plan(Six, AudioCheckPowerModes.DummyLoad, 50, false, AudioCheckListenMethods.MicTest));
        Assert.Equal(Leaves, Plan(Six, AudioCheckPowerModes.DummyLoad, 0, true, AudioCheckListenMethods.MicTest));
    }

    [Fact]
    public void Test_your_microphone_is_the_only_exception_there_is()
    {
        foreach (AudioCheckListenMethods method in Enum.GetValues<AudioCheckListenMethods>())
            Assert.Equal(method == AudioCheckListenMethods.MicTest, RadioPowerFloor.IsZeroWattException(method));
    }

    [Theory]
    [InlineData(false)]   // local
    [InlineData(true)]    // SmartLink, the way a 6300 is often reached
    public void Test_your_microphone_stays_offered_on_a_6300(bool remote) =>
        Assert.Contains(AudioCheckListenMethods.MicTest, AudioCheckMethods.Offered(true, remote, "one-scu-not-confirmed", false));

    // ── Low power, on a 6300 ────────────────────────────────────────────

    [Fact]
    public void A_6300_whose_own_power_is_zero_is_raised_for_low_power() =>
        Assert.Equal(Sets(1), Plan(Six, AudioCheckPowerModes.LowPower, 0, false));

    [Theory]
    [InlineData(AudioCheckPowerModes.LowPower)]
    [InlineData(AudioCheckPowerModes.DummyLoad)]
    public void The_operators_own_dummy_load_does_not_silence_a_6300(AudioCheckPowerModes mode) =>
        Assert.Equal(Sets(1), Plan(Six, mode, 0, true));

    [Theory]
    [InlineData(50, 10, 10)]   // still a cap above the floor
    [InlineData(11, 10, 10)]
    public void Low_power_is_still_a_cap_on_a_6300(int current, int low, int expected) =>
        Assert.Equal(Sets(expected), Plan(Six, AudioCheckPowerModes.LowPower, current, false, low: low));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Low_power_never_raises_a_6300_that_is_already_above_zero(int current) =>
        Assert.Equal(Leaves, Plan(Six, AudioCheckPowerModes.LowPower, current, false));

    [Fact]
    public void A_field_set_to_its_lowest_keeps_a_6300_at_one_watt() =>
        Assert.Equal(Sets(1), Plan(Six, AudioCheckPowerModes.LowPower, 40, false, low: 1));

    // ── Every other radio moves not at all ──────────────────────────────

    [Theory]
    [InlineData(Eight)]
    [InlineData("FLEX-6400")]
    [InlineData("FLEX-6600")]
    [InlineData("AU-510")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_radio_keeps_its_zero_and_its_cap(string model)
    {
        Assert.Equal(0, RadioPowerFloor.Watts(model));
        Assert.Equal(Engages, Plan(model, AudioCheckPowerModes.DummyLoad, 50, false));
        Assert.Equal(Leaves, Plan(model, AudioCheckPowerModes.DummyLoad, 0, true));
        Assert.Equal(Leaves, Plan(model, AudioCheckPowerModes.LowPower, 0, false));   // only ever lowers
        Assert.Equal(Leaves, Plan(model, AudioCheckPowerModes.LowPower, 0, true));
        Assert.Equal(Leaves, Plan(model, AudioCheckPowerModes.LowPower, 50, true));   // cannot override the load
        Assert.Equal(Sets(10), Plan(model, AudioCheckPowerModes.LowPower, 50, false));
    }

    [Theory]
    [InlineData("flex-6300")]
    [InlineData(" FLEX-6300 ")]
    public void The_model_is_read_as_the_radio_spells_it(string model) =>
        Assert.True(RadioPowerFloor.Applies(model));

    // ── What the operator reads and hears ───────────────────────────────

    [Fact]
    public void A_6300_has_only_low_power_and_a_saved_dummy_load_shows_as_one_watt()
    {
        Assert.Equal(AudioCheckPowerModes.LowPower, AudioCheckPowerRule.ShownPowerMode(Six, AudioCheckPowerModes.DummyLoad));
        Assert.Equal(1, AudioCheckPowerRule.ShownWatts(Six, AudioCheckPowerModes.DummyLoad, 10));
        // A level the operator chose stands.
        Assert.Equal(AudioCheckPowerModes.LowPower, AudioCheckPowerRule.ShownPowerMode(Six, AudioCheckPowerModes.LowPower));
        Assert.Equal(25, AudioCheckPowerRule.ShownWatts(Six, AudioCheckPowerModes.LowPower, 25));
    }

    [Fact]
    public void Any_other_radio_shows_what_was_saved()
    {
        Assert.Equal(AudioCheckPowerModes.DummyLoad, AudioCheckPowerRule.ShownPowerMode(Eight, AudioCheckPowerModes.DummyLoad));
        Assert.Equal(10, AudioCheckPowerRule.ShownWatts(Eight, AudioCheckPowerModes.DummyLoad, 10));
        Assert.Equal(1, AudioCheckPowerRule.ShownWatts(Eight, AudioCheckPowerModes.LowPower, 0));   // the field's own clamp
    }

    [Fact]
    public void A_typed_zero_is_refused_on_a_6300_and_says_this_rig()
    {
        Assert.Equal("You can't use this audio check method at 0 power on this rig.", AudioCheckPowerRule.TypedWattsRefusal(Six, 0));
        Assert.Null(AudioCheckPowerRule.TypedWattsRefusal(Six, 1));
        Assert.Null(AudioCheckPowerRule.TypedWattsRefusal(Eight, 0));   // clamps to 1, as it always has
        Assert.Equal("You can't use 0 watts on this rig.", RadioPowerFloor.TypedPowerRefusal(Six, 0));
        Assert.Null(RadioPowerFloor.TypedPowerRefusal(Six, 1));
        Assert.Null(RadioPowerFloor.TypedPowerRefusal(Eight, 0));
    }

    [Fact]
    public void Nothing_said_about_this_names_the_model()
    {
        foreach (string text in new[]
        {
            AudioCheckPowerRule.TypedWattsRefusal(Six, 0), RadioPowerFloor.TypedPowerRefusal(Six, 0),
            RadioPowerFloor.DummyLoadUnavailable,
        })
        {
            Assert.DoesNotContain("6300", text);
            Assert.Contains("this rig", text);
        }
    }

    // ── What a press does to a power field ──────────────────────────────

    [Theory]
    [InlineData(5, -1, 4)]
    [InlineData(5, -10, 1)]   // Page Down stops at the floor
    [InlineData(1, -1, 1)]    // at the floor
    [InlineData(0, -1, 0)]    // the radio reads zero: Down writes nothing
    [InlineData(0, -10, 0)]
    [InlineData(0, 1, 1)]     // Up leaves zero behind
    [InlineData(0, 5, 5)]
    [InlineData(99, 5, 100)]
    public void Presses_on_a_power_field_with_a_floor(int value, int delta, int expected) =>
        Assert.Equal(expected, ValueFieldRules.Adjusted(value, delta, 0, 100, 1));

    [Theory]
    [InlineData(1, -1, 0)]
    [InlineData(0, -1, 0)]
    public void Without_a_floor_a_field_goes_to_its_minimum(int value, int delta, int expected) =>
        Assert.Equal(expected, ValueFieldRules.Adjusted(value, delta, 0, 100, 0));

    // ── One watt, five watts ─────────────────────────────────────────────

    [Theory]
    [InlineData("1", "watts", "watt")]
    [InlineData("5", "watts", "watts")]
    [InlineData("0", "watts", "watts")]
    [InlineData("1", "seconds", "second")]
    [InlineData("-1", "seconds", "second")]
    [InlineData("1.00", "watts", "watts")]   // decimals read as a plural
    [InlineData("1", "dB", "dB")]
    [InlineData("1", "dBm", "dBm")]
    [InlineData("1", "dBFS", "dBFS")]
    [InlineData("1", "percent", "percent")]
    [InlineData("1", "hertz", "hertz")]
    [InlineData("1", "ms", "ms")]
    public void A_unit_agrees_with_its_value(string value, string unit, string expected) =>
        Assert.Equal(expected, ValueFieldRules.UnitFor(value, unit));

    [Fact]
    public void The_field_says_it_on_screen_and_in_speech_through_one_suffix()
    {
        string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Controls", "ValueFieldControl.xaml.cs"));
        Assert.Contains("ValueFieldRules.UnitFor(FormatValue(_value), Unit)", src);
        Assert.DoesNotContain("(\"unit\", Unit)", src);
    }

    [Fact]
    public void The_Power_dialog_says_the_range_this_rig_has()
    {
        string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "PowerDialog.xaml.cs"));
        Assert.Matches(@"RadioPowerFloor\.Applies\(_rig\.RadioModel\)\s+\? ""audio\.power\.range_watts_floor""", src);
    }

    // ── Ctrl+Enter on the check's fields ────────────────────────────────

    [Fact]
    public void Ctrl_Enter_finishes_a_typed_value_before_it_starts_or_stops_anything()
    {
        string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.cs"));
        int key = src.IndexOf("Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter", StringComparison.Ordinal);
        int commit = src.IndexOf("typing?.CommitPendingEntry()", key, StringComparison.Ordinal);
        int act = src.IndexOf("case PressAct.Stop:", key, StringComparison.Ordinal);
        Assert.True(key > 0 && commit > key && commit < act);
    }

    // ── The rig ─────────────────────────────────────────────────────────

    internal static FlexBase RigOf(string model)
    {
        var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var radio = (Flex.Smoothlake.FlexLib.Radio)Activator.CreateInstance(
            typeof(Flex.Smoothlake.FlexLib.Radio), Private, binder: null, args: new object[] { false }, culture: null)!;
        typeof(Flex.Smoothlake.FlexLib.Radio).GetField("_model", Private)!.SetValue(radio, model);
        rig.theRadio = radio;
        return rig;
    }

    internal static void Done(FlexBase rig) { rig.theRadio = null; try { rig.Dispose(); } catch { } }

    // ── A press at a reported zero (ruled 2026-11-01 13:04) ─────────────

    [Theory]
    [InlineData(Six, true, 0, true)]
    [InlineData(Six, false, 0, false)]          // the connect's placeholder: key as always
    [InlineData(Six, true, 1, false)]
    [InlineData(Six, false, 1, false)]
    [InlineData("flex-6300", true, 0, true)]    // as Applies reads a model
    [InlineData(Eight, true, 0, false)]         // measured to work at zero
    [InlineData("FLEX-6400", true, 0, false)]   // unmeasured: nothing changes
    [InlineData("FLEX-6600", true, 0, false)]
    [InlineData("FLEX-8400", true, 0, false)]
    [InlineData("AU-510", true, 0, false)]
    [InlineData("", true, 0, false)]            // absent reads as not floored
    [InlineData("   ", true, 0, false)]
    [InlineData(null, true, 0, false)]
    public void A_press_refuses_only_on_a_floored_rig_at_a_reported_zero(string? model, bool reported, int watts, bool refused) =>
        Assert.Equal(refused, RadioPowerFloor.RefusesPress(model, reported, watts));

    [Fact]
    public void The_refusals_say_this_rig_and_never_the_model()
    {
        Assert.Equal("This rig is at 0 watts. Set your power first.", RadioPowerFloor.TransmitRefusal);
        Assert.Equal("This rig's tune power is at 0 watts. Set your tune power first.", RadioPowerFloor.TuneRefusal);
    }

    [Fact]
    public void Dummy_load_mode_refuses_to_set_on_a_6300_and_on_no_other_radio()
    {
        var six = RigOf(Six);
        var eight = RigOf(Eight);
        try
        {
            six.DummyLoadMode = true;
            Assert.False(six.DummyLoadMode);
            eight.DummyLoadMode = true;      // positive control: the setter is reachable
            Assert.True(eight.DummyLoadMode);
        }
        finally { Done(six); Done(eight); }
    }

    [Fact]
    public void Only_Test_your_microphone_gets_through_the_refusal()
    {
        foreach (AudioCheckListenMethods method in Enum.GetValues<AudioCheckListenMethods>())
        {
            var rig = RigOf(Six);
            try
            {
                using (rig.ZeroWattException(method)) rig.DummyLoadMode = true;
                Assert.Equal(method == AudioCheckListenMethods.MicTest, rig.DummyLoadMode);
                // The way through closes behind it.
                rig.DummyLoadMode = false;
                rig.DummyLoadMode = true;
                Assert.False(rig.DummyLoadMode);
            }
            finally { Done(rig); }
        }
    }

    [Fact]
    public void A_6300_says_why_dummy_load_mode_is_missing_and_no_other_radio_mentions_it()
    {
        var six = RigOf(Six);
        var eight = RigOf(Eight);
        try
        {
            Assert.Contains("Dummy load mode: unavailable - Dummy load mode isn't available on this rig.", six.BuildFeatureAvailabilityText());
            Assert.DoesNotContain("Dummy load", eight.BuildFeatureAvailabilityText());
        }
        finally { Done(six); Done(eight); }
    }

    [Fact]
    public void The_way_through_has_one_caller()
    {
        var callers = Directory.GetFiles(IntegrationPassTree.Root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !f.EndsWith("Tests.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(".ZeroWattException("))
            .Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "CheckYourAudioDialog.Check.cs" }, callers);
    }

    [Fact]
    public void The_menu_keeps_the_item_and_says_why()
    {
        string menu = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "NativeMenuBar.cs"));
        int item = menu.IndexOf("\"Dummy &Load Mode\"", StringComparison.Ordinal);
        int asks = menu.IndexOf("Radios.RadioPowerFloor.Applies(Rig.RadioModel)", item, StringComparison.Ordinal);
        int says = menu.IndexOf("SpeakAfterMenuClose(Radios.RadioPowerFloor.DummyLoadUnavailable);", item, StringComparison.Ordinal);
        int write = menu.IndexOf("Rig.DummyLoadMode = !Rig.DummyLoadMode;", item, StringComparison.Ordinal);
        Assert.True(item > 0 && asks > item && says > asks && write > says, "the menu must answer before it writes");
    }

    [Theory]
    [InlineData("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs", "PowerFieldFloor.Apply(_txPowerControl, () => _rig, () => !_txPowerXvtrMode);")]
    [InlineData("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs", "PowerFieldFloor.Apply(_tunePowerControl, () => _rig);")]
    [InlineData("JJFlexWpf/Dialogs/PowerDialog.xaml.cs", "PowerFieldFloor.Apply(RfPowerField, () => _rig, () => !_xvtrMode);")]
    [InlineData("JJFlexWpf/Dialogs/PowerDialog.xaml.cs", "PowerFieldFloor.Apply(TunePowerField, () => _rig);")]
    public void Every_live_power_field_takes_the_floor(string file, string line) =>
        Assert.Contains(line, File.ReadAllText(Path.Combine(IntegrationPassTree.Root, file)));

    // ── The field, read from its source: it cannot be built on a live desk ──

    private static (TypedEntry Entry, int Value, string Refused) Typed(string model, bool parsed, int typed)
    {
        // The check's own field: 1 to 100, with the rule's refusal.
        TypedEntry entry = ValueFieldRules.Judge(parsed, typed, 1, 100,
            t => AudioCheckPowerRule.TypedWattsRefusal(model, t), out int value, out string refused);
        return (entry, value, refused);
    }

    [Theory]
    [InlineData(Six, 5)]
    [InlineData(Eight, 5)]
    [InlineData(Six, 1)]
    [InlineData(Eight, 100)]
    public void A_number_in_range_is_taken_as_typed(string model, int typed)
    {
        var r = Typed(model, true, typed);
        Assert.Equal(TypedEntry.Taken, r.Entry);
        Assert.Equal(typed, r.Value);
    }

    [Fact]
    public void A_typed_zero_is_refused_on_a_floored_model()
    {
        var r = Typed(Six, true, 0);
        Assert.Equal(TypedEntry.Refused, r.Entry);
        Assert.Equal("You can't use this audio check method at 0 power on this rig.", r.Refused);
    }

    [Theory]
    [InlineData(Eight, 0, 1)]     // clamps as it always has, and must not start a check
    [InlineData(Eight, 101, 100)]
    [InlineData(Six, 101, 100)]
    public void A_number_past_an_end_is_clamped_and_says_it_was(string model, int typed, int value)
    {
        var r = Typed(model, true, typed);
        Assert.Equal(TypedEntry.Clamped, r.Entry);
        Assert.Equal(value, r.Value);
    }

    [Theory]
    [InlineData(Six)]
    [InlineData(Eight)]
    public void What_is_not_a_number_is_said_to_be_not_a_number(string model) =>
        Assert.Equal(TypedEntry.NotANumber, Typed(model, false, 0).Entry);

    [Fact]
    public void Only_the_number_as_typed_lets_the_press_go_on()
    {
        string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Controls", "ValueFieldControl.xaml.cs"));
        Assert.Contains("internal bool CommitPendingEntry() => !_numberEntryMode || ConfirmNumberEntry() == TypedEntry.Taken;", src);
    }

    [Fact]
    public void Every_field_goes_on_clamping_and_nothing_is_saved_for_a_refused_entry()
    {
        string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Controls", "ValueFieldControl.xaml.cs"));
        int method = src.IndexOf("private TypedEntry ConfirmNumberEntry()", StringComparison.Ordinal);
        int judge = src.IndexOf("ValueFieldRules.Judge(parsed, val, _min, _max, TypedValueRefusal,", method, StringComparison.Ordinal);
        int taken = src.IndexOf("_value = value;", judge, StringComparison.Ordinal);
        int changed = src.IndexOf("ValueChanged?.Invoke(this, _value);", taken, StringComparison.Ordinal);
        int fallThrough = src.IndexOf("default:", judge, StringComparison.Ordinal);
        Assert.True(judge > method && fallThrough > judge && taken > fallThrough && changed > taken);
    }

    [Fact]
    public void The_watts_field_asks_the_rule_and_a_loaded_choice_cannot_save_itself()
    {
        string check = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"));
        Assert.Contains("_checkWattsControl.TypedValueRefusal = typed => AudioCheckPowerRule.TypedWattsRefusal(_rig?.RadioModel, typed);", check);
        int load = check.IndexOf("private void LoadPerRadioPrefs()", StringComparison.Ordinal);
        int shown = check.IndexOf("AudioCheckPowerRule.ShownPowerMode(_rig.RadioModel, _radioCfg.AudioCheckPowerMode)", load, StringComparison.Ordinal);
        Assert.True(shown > load);
        // Set between the two SuppressEvents, so the load writes nothing back.
        Assert.Contains("_checkPowerControl.SuppressEvents = true;", check.Substring(load, shown - load));
    }

    [Fact]
    public void A_transverter_field_is_dBm_and_keeps_its_zero()
    {
        string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Controls", "PowerFieldFloor.cs"));
        Assert.Contains("inWatts?.Invoke() == false ? int.MinValue :", src);
        Assert.Matches(@"TypedValueRefusal = typed => inWatts\?\.Invoke\(\) == false\s+\? null :", src);
    }

    // ── The session itself: the real StartAsync against the inert host ──

    [Theory]
    // model, method, mode, power, operator's dummy load -> at the key, dummy at the key, power after restores
    [InlineData(Six, "RecordPlayback", "DummyLoad", 50, false, 1, 0, 50)]
    [InlineData(Six, "Monitor", "DummyLoad", 50, false, 1, 0, 50)]
    [InlineData(Six, "RecordPlayback", "LowPower", 0, false, 1, 0, 0)]
    [InlineData(Six, "RecordPlayback", "LowPower", 50, false, 10, 0, 50)]
    [InlineData(Six, "RecordPlayback", "LowPower", 5, false, 5, 0, 5)]
    [InlineData(Six, "RecordPlayback", "LowPower", 0, true, 1, 1, 0)]
    [InlineData(Six, "RecordPlayback", "DummyLoad", 0, true, 1, 1, 0)]
    [InlineData(Six, "MicTest", "DummyLoad", 50, false, 0, 1, 50)]   // the exception: rides the dummy load, and comes back
    public async Task A_check_on_a_6300_keys_where_the_rule_says_and_every_power_comes_back(string model, string method, string mode,
        int power, bool dummy, int atKey, int dummyAtKey, int after)
    {
        int[] r = await Run($"power:{model}:{method}:{mode}:{power}:{(dummy ? 1 : 0)}");
        Assert.Equal(new[] { atKey, dummyAtKey, 1, after }, r);
    }

    [Theory]
    [InlineData(Eight, "RecordPlayback", "DummyLoad", 50, false, 0)]
    [InlineData(Eight, "RecordPlayback", "LowPower", 0, false, 0)]
    public async Task Zero_stays_zero_where_the_ruling_leaves_it(string model, string method, string mode, int power, bool dummy, int atKey)
    {
        int[] r = await Run($"power:{model}:{method}:{mode}:{power}:{(dummy ? 1 : 0)}");
        Assert.Equal(atKey, r[0]);
        Assert.Equal(1, r[2]);   // positive control: it keyed
    }

    [Fact]
    public async Task An_operator_who_releases_their_dummy_load_during_the_check_keeps_their_power()
    {
        // The dummy load's release has already written their power back; the
        // zero the check saved must not overwrite it.
        int[] r = await Run($"power:{Six}:RecordPlayback:LowPower:0:1:release");
        Assert.Equal(1, r[0]);
        Assert.Equal(42, r[3]);
    }

    // ── An unreported power is not a measured zero ──────────────────────

    [Fact]
    public async Task A_check_never_acts_on_a_power_the_radio_has_not_reported()
    {
        // Keys, power writes, restores registered, sentences said.
        Assert.Equal(new[] { 0, 0, 0, 1 }, await Run("power-unreported"));
    }

    [Fact]
    public async Task A_tune_that_comes_up_during_the_count_keys_nothing_and_arms_nothing()
    {
        Assert.Equal(new[] { 0, 0, 0, 1 }, await Run("tune-during-count"));
    }

    [Fact]
    public async Task A_reported_zero_is_a_reading_and_takes_the_raise()
    {
        // The positive control: same radio, same zero, the radio has said so.
        int[] r = await Run($"power:{Six}:Monitor:LowPower:0:0");
        Assert.Equal(1, r[0]);
        Assert.Equal(1, r[2]);
        Assert.Equal(0, r[3]);
    }

    /// <summary>The real path: FlexLib parses a status line and raises the same change the rig hears at a connect.</summary>
    private static void Wire(FlexBase rig)
    {
        var radio = rig.theRadio!;
        var binding = new StationConnect.ObservationBindingFor(radio).Binding;
        var handler = typeof(FlexBase).GetMethod("radioPropertyChangedHandler", Flags)!;
        radio.PropertyChanged += (s, e) => handler.Invoke(rig, new object[] { s!, e, binding });
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Status(FlexBase rig, string line) =>
        typeof(Flex.Smoothlake.FlexLib.Radio).GetMethod("ParseTransmitStatus", Flags)!.Invoke(rig.theRadio, new object[] { line });

    private static void TransmitProfileReported(FlexBase rig, string name) =>
        typeof(Flex.Smoothlake.FlexLib.Radio).GetMethod("ParseProfilesStatus", Flags)!.Invoke(rig.theRadio, new object[] { "tx current=" + name });

    private static FlexBase WiredRig()
    {
        var rig = RigOf(Six);
        Wire(rig);
        return rig;
    }

    private static FlexBase WiredRig(string model)
    {
        var rig = RigOf(model);
        Wire(rig);
        return rig;
    }

    [Fact]
    public void A_press_on_the_connects_placeholder_keys_as_always()
    {
        var rig = WiredRig();
        try
        {
            Status(rig, "rfpower=0 tunepower=0");
            Assert.False(rig.TransmitPressRefused);
            Assert.False(rig.TunePressRefused);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void A_press_at_a_zero_the_radio_really_reports_refuses()
    {
        var rig = WiredRig();
        try
        {
            Status(rig, "rfpower=0 tunepower=0");
            TransmitProfileReported(rig, "Default");
            Assert.True(rig.TransmitPressRefused);     // the positive control for the placeholder
            Assert.True(rig.TunePressRefused);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void Time_alone_never_turns_a_placeholder_into_a_refusal()
    {
        var rig = WiredRig();
        long now = 500_000;
        rig.PowerReportClock = () => now;
        try
        {
            Status(rig, "rfpower=0 tunepower=0");
            now += FlexBase.PlaceholderWindowMs * 100;
            Assert.True(rig.CheckPowersReported);       // the window has closed for a check
            Assert.False(rig.TransmitPressRefused);     // and still proves nothing to a key
            Assert.False(rig.TunePressRefused);

            TransmitProfileReported(rig, "Default");    // the positive control: late evidence counts
            Assert.True(rig.TransmitPressRefused);
            Assert.True(rig.TunePressRefused);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void A_late_real_report_after_the_window_stays_unrefused()
    {
        var rig = WiredRig();
        long now = 500_000;
        rig.PowerReportClock = () => now;
        try
        {
            Status(rig, "rfpower=0 tunepower=0");
            now += FlexBase.PlaceholderWindowMs * 100;
            Status(rig, "rfpower=50 tunepower=10");
            TransmitProfileReported(rig, "Default");
            Assert.False(rig.TransmitPressRefused);
            Assert.False(rig.TunePressRefused);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void A_zero_that_follows_a_real_report_refuses_with_no_profile_heard()
    {
        var rig = WiredRig();
        try
        {
            Status(rig, "rfpower=50 tunepower=10");
            Assert.False(rig.TransmitPressRefused);
            Status(rig, "rfpower=0 tunepower=0");       // set by another client
            Assert.True(rig.TransmitPressRefused);
            Assert.True(rig.TunePressRefused);
        }
        finally { Done(rig); }
    }

    [Theory]
    [InlineData("rfpower=0 tunepower=10", true, false)]
    [InlineData("rfpower=50 tunepower=0", false, true)]
    [InlineData("rfpower=50 tunepower=10", false, false)]
    public void The_two_powers_are_read_apart(string line, bool transmit, bool tune)
    {
        var rig = WiredRig();
        try
        {
            Status(rig, line);
            TransmitProfileReported(rig, "Default");
            Assert.Equal(transmit, rig.TransmitPressRefused);
            Assert.Equal(tune, rig.TunePressRefused);
        }
        finally { Done(rig); }
    }

    [Theory]
    [InlineData(Eight)]
    [InlineData("FLEX-6700")]
    [InlineData("")]
    public void A_rig_with_no_floor_never_refuses_at_a_reported_zero(string model)
    {
        var rig = WiredRig(model);
        try
        {
            Status(rig, "rfpower=0 tunepower=0");
            TransmitProfileReported(rig, "Default");
            Assert.True(rig.CheckPowersReported);      // it really is a reported zero
            Assert.False(rig.TransmitPressRefused);
            Assert.False(rig.TunePressRefused);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void A_placeholder_zero_is_not_a_report()
    {
        var rig = WiredRig();
        try
        {
            Assert.False(rig.TransmitPowerReported);       // created
            Status(rig, "rfpower=0 tunepower=0");          // what every connect opens with
            Status(rig, "rfpower=0 tunepower=0");
            Assert.False(rig.TransmitPowerReported);       // each on its own
            Assert.False(rig.TunePowerReported);
            Assert.False(rig.CheckPowersReported);
            Assert.Equal(0, rig.XmitPower);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void The_real_pair_is_a_report_as_soon_as_it_arrives()
    {
        var rig = WiredRig();
        try
        {
            Status(rig, "rfpower=0 tunepower=0");
            Status(rig, "rfpower=100 tunepower=10");       // ahead of the profile, as measured
            Assert.True(rig.CheckPowersReported);
            Assert.Equal(100, rig.XmitPower);
            Assert.Equal(10, rig.TunePower);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void A_radio_really_at_zero_is_a_report_once_its_profile_has_come()
    {
        var rig = WiredRig();
        try
        {
            Status(rig, "rfpower=0 tunepower=0");
            Assert.False(rig.CheckPowersReported);
            TransmitProfileReported(rig, "Default");
            Assert.True(rig.CheckPowersReported);          // the positive control for the placeholder test
            Assert.Equal(0, rig.XmitPower);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void A_radio_at_zero_with_no_profile_is_believed_after_the_window()
    {
        var rig = WiredRig();
        long now = 500_000;
        rig.PowerReportClock = () => now;
        try
        {
            Status(rig, "rfpower=0 tunepower=0");
            now += FlexBase.PlaceholderWindowMs - 1;
            Status(rig, "rfpower=0 tunepower=0");      // a later report does not restart it
            Assert.False(rig.CheckPowersReported);     // the positive control: still held off
            now += 1;
            Assert.True(rig.CheckPowersReported);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void The_window_is_longer_than_any_connect_read() =>
        Assert.True(FlexBase.PlaceholderWindowMs > 3343);

    [Fact]
    public void Nothing_opens_the_window_but_a_report_from_the_radio()
    {
        var rig = WiredRig();
        long now = 500_000;
        rig.PowerReportClock = () => now;
        try
        {
            now += FlexBase.PlaceholderWindowMs * 2;   // no report at all
            Assert.False(rig.CheckPowersReported);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void A_new_connection_starts_its_window_again()
    {
        var rig = WiredRig();
        long now = 500_000;
        rig.PowerReportClock = () => now;
        try
        {
            Status(rig, "rfpower=0 tunepower=0");
            now += FlexBase.PlaceholderWindowMs;
            Assert.True(rig.CheckPowersReported);
            typeof(FlexBase).GetMethod("ForgetPowerReports", Flags)!.Invoke(rig, null);
            Status(rig, "rfpower=0 tunepower=0");      // the new connection's placeholder
            Assert.False(rig.CheckPowersReported);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void A_new_connection_starts_from_nothing_on_the_same_rig()
    {
        var rig = WiredRig();
        try
        {
            Status(rig, "rfpower=50 tunepower=9");
            TransmitProfileReported(rig, "Default");
            Assert.True(rig.CheckPowersReported);          // the positive control

            typeof(FlexBase).GetMethod("ForgetPowerReports", Flags)!.Invoke(rig, null);
            Assert.False(rig.CheckPowersReported);
            Status(rig, "rfpower=0 tunepower=0");          // the new connection's placeholder
            Assert.False(rig.TransmitPowerReported);       // the old profile cannot vouch for it
            Assert.False(rig.TunePowerReported);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void A_write_of_ours_is_not_a_report()
    {
        var rig = WiredRig();
        try
        {
            // The rig's own write, through the path XmitPower queues.
            var mark = typeof(FlexBase).GetMethod("OurPowerWrite", BindingFlags.Static | BindingFlags.NonPublic)!;
            using (var ours = (IDisposable)mark.Invoke(null, null)!)
            {
                rig.theRadio!.RFPower = 7;
                rig.theRadio.TunePower = 8;
            }
            Assert.Equal(7, rig.XmitPower);                // the change did arrive
            Assert.False(rig.TransmitPowerReported);
            Assert.False(rig.TunePowerReported);

            Status(rig, "rfpower=50 tunepower=9");
            Assert.True(rig.CheckPowersReported);
            Assert.Equal(50, rig.XmitPower);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void Tune_power_has_its_own_fact_because_a_dummy_load_saves_both()
    {
        var rig = WiredRig();
        try
        {
            Status(rig, "rfpower=50");
            Assert.True(rig.TransmitPowerReported);
            Assert.False(rig.TunePowerReported);
            Assert.False(rig.CheckPowersReported);
            Status(rig, "tunepower=25");
            Assert.True(rig.CheckPowersReported);
        }
        finally { Done(rig); }
    }

    [Fact]
    public void The_rigs_two_power_writes_are_marked_and_are_the_only_ones()
    {
        var offenders = Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, "Radios"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(f => File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//") &&
                System.Text.RegularExpressions.Regex.IsMatch(l, @"(theRadio|radio|\br)\.(RFPower|TunePower)\s*=[^=]")
                && !l.Contains("using (OurPowerWrite())")).Select(l => Path.GetFileName(f) + ": " + l.Trim()))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("ForgetPowerReports();\r\n            _txAllowedReported = false;\r\n\r\n            // Fresh connection: whatever firmware")]
    [InlineData("_txAllowedReported = false;\r\n            ForgetPowerReports();\r\n\r\n            var sw")]
    [InlineData("if (!nowConnected) ForgetPowerReports();")]
    public void The_report_ends_with_the_connection(string where) => Assert.Contains(where.Replace("\r\n", "\n"),
        File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.cs")).Replace("\r\n", "\n"));

    [Fact]
    public void The_door_asks_before_the_count_and_the_power_reads_only_after_the_gate()
    {
        string check = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"));
        int toggle = check.IndexOf("public void ToggleAudioCheck()", StringComparison.Ordinal);
        int door = check.IndexOf("!_rig.CheckPowersReported", toggle, StringComparison.Ordinal);
        // Test your microphone saves both powers, so only the two arrangements are left out.
        Assert.Contains("SelectedMethod is not (AudioCheckListenMethods.OffAir or AudioCheckListenMethods.Loopback)", check.Substring(door, 120));
        int start = check.IndexOf("_ = StartAudioCheckSessionAsync(", toggle, StringComparison.Ordinal);
        Assert.True(door > toggle && door < start);
        int session = check.IndexOf("public async Task<bool> StartAsync(", StringComparison.Ordinal);
        int gate = check.IndexOf("!rig.CheckPowersReported", session, StringComparison.Ordinal);
        int read = check.IndexOf("int currentPower = rig.XmitPower;", session, StringComparison.Ordinal);
        int arm = check.IndexOf("rig.StartSliceRecordAsync(", session, StringComparison.Ordinal);
        Assert.True(gate > session && gate < read && gate < arm);
    }

    private static async Task<int[]> Run(string scenario)
    {
        var source = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"));
        var tree = CSharpSyntaxTree.ParseText(source).GetRoot();
        string Method(string name) => tree.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name).ToFullString();
        string host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AudioCheckStartHost.txt"));
        foreach (var name in new[] { "StartLoopbackCheckAsync", "StartAudioCheckSessionAsync", "StartAsync", "StopCheck" })
            host = host.Replace("/*" + name + "*/", Method(name));
        var offAir = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(IntegrationPassTree.Root,
            "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.OffAir.cs"))).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "StartOffAirAsync").ToFullString();
        host = host.Replace("/*StartOffAirAsync*/", offAir);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(x => MetadataReference.CreateFromFile(x)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(FlexBase).Assembly.Location));
        var compilation = CSharpCompilation.Create("PowerFloor_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(host) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));
        var assembly = Assembly.Load(output.ToArray());
        return await (Task<int[]>)assembly.GetType("Host")!.GetMethod("Run")!.Invoke(null, new object[] { scenario })!;
    }
}
