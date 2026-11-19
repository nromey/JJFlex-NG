using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Check your audio's low power level is a CAP: it only ever lowers. Set above
/// the radio's power it changes nothing, and until #567 nothing said so; the
/// operator went hunting for a fault that was not there. These pin the one case
/// that now speaks, and the two other "nothing to do" cases of the same plan
/// that must not: the operator's own dummy load, and the floor on a radio that
/// cannot run at zero.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CheckPowerCapNoticeTests
{
    private const string Six = "FLEX-6300";
    private const string Eight = "FLEX-8600";

    private static bool Unchanged(string model, AudioCheckPowerModes mode, int low, int current, bool dummy,
        AudioCheckListenMethods method = AudioCheckListenMethods.RecordPlayback) =>
        AudioCheckPowerRule.CapLeavesPowerUnchanged(model, method, mode, low, current, dummy);

    [Theory]
    [InlineData(Eight)]
    [InlineData(Six)]   // above its floor, the cap is a cap on a 6300 too
    public void A_cap_above_the_radios_power_is_the_case_that_speaks(string model)
    {
        Assert.Equal(AudioCheckPowerAct.None,
            AudioCheckPowerRule.Plan(model, AudioCheckListenMethods.RecordPlayback, AudioCheckPowerModes.LowPower, 100, 5, false).Act);
        Assert.True(Unchanged(model, AudioCheckPowerModes.LowPower, 100, 5, false));
    }

    [Theory]
    [InlineData(AudioCheckListenMethods.Monitor)]
    [InlineData(AudioCheckListenMethods.RecordPlayback)]
    public void Both_methods_that_obey_the_choice_speak_it(AudioCheckListenMethods method) =>
        Assert.True(Unchanged(Eight, AudioCheckPowerModes.LowPower, 50, 10, false, method));

    [Fact]
    public void A_cap_equal_to_the_power_asked_for_what_the_radio_has()
    {
        // Nothing changed, but nothing was asked that did not happen.
        Assert.False(Unchanged(Eight, AudioCheckPowerModes.LowPower, 10, 10, false));
    }

    [Fact]
    public void A_cap_below_the_power_lowers_it_and_says_nothing_extra()
    {
        Assert.Equal(new AudioCheckPowerStep(AudioCheckPowerAct.SetPower, 10),
            AudioCheckPowerRule.Plan(Eight, AudioCheckListenMethods.RecordPlayback, AudioCheckPowerModes.LowPower, 10, 50, false));
        Assert.False(Unchanged(Eight, AudioCheckPowerModes.LowPower, 10, 50, false));
    }

    [Theory]
    [InlineData(Eight)]
    [InlineData(Six)]
    public void The_operators_own_dummy_load_is_never_this_case(string model)
    {
        // A dummy load left on reads zero, and every level is above zero: the
        // naive comparison would say "nothing changed" on a radio with no RF.
        Assert.False(Unchanged(model, AudioCheckPowerModes.LowPower, 100, 0, true));
        Assert.False(Unchanged(model, AudioCheckPowerModes.LowPower, 100, 5, true));
    }

    [Fact]
    public void A_power_of_zero_is_never_this_case()
    {
        // Zero is the dummy load's reading, or the placeholder before a radio's
        // first real report; neither is a level the operator meant.
        Assert.False(Unchanged(Eight, AudioCheckPowerModes.LowPower, 100, 0, false));
    }

    [Fact]
    public void On_a_floored_radio_the_floor_decides_and_this_says_nothing()
    {
        // Below the floor, the plan raises to it: not a no-op.
        Assert.Equal(new AudioCheckPowerStep(AudioCheckPowerAct.SetPower, 1),
            AudioCheckPowerRule.Plan(Six, AudioCheckListenMethods.RecordPlayback, AudioCheckPowerModes.LowPower, 100, 0, false));
        Assert.False(Unchanged(Six, AudioCheckPowerModes.LowPower, 100, 0, false));
        // The Dummy load choice at the floor leaves power alone because of the
        // floor, whatever the low power level says.
        Assert.Equal(AudioCheckPowerAct.None,
            AudioCheckPowerRule.Plan(Six, AudioCheckListenMethods.RecordPlayback, AudioCheckPowerModes.DummyLoad, 100, 1, false).Act);
        Assert.False(Unchanged(Six, AudioCheckPowerModes.DummyLoad, 100, 1, false));
    }

    [Fact]
    public void The_dummy_load_choice_and_test_your_microphone_never_speak_it()
    {
        Assert.False(Unchanged(Eight, AudioCheckPowerModes.DummyLoad, 100, 5, false));
        Assert.False(Unchanged(Eight, AudioCheckPowerModes.DummyLoad, 100, 0, true));
        Assert.False(Unchanged(Six, AudioCheckPowerModes.DummyLoad, 100, 5, false, AudioCheckListenMethods.MicTest));
    }

    [Fact]
    public void The_sentence_names_both_powers_and_says_nothing_changed()
    {
        string said = AudioCheckPowerRule.CapAboveCurrentNotice(100, 5);
        Assert.Contains("100", said);
        Assert.Contains("5 watts", said);
        Assert.Contains("nothing changed", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", said);
    }

    [Fact]
    public void The_label_is_not_the_old_one_that_read_as_a_target()
    {
        Assert.False(string.IsNullOrWhiteSpace(AudioCheckPowerRule.LowPowerLevelLabel));
        Assert.NotEqual("Low power level", AudioCheckPowerRule.LowPowerLevelLabel);
    }

    [Fact]
    public void The_check_says_it_once_after_it_is_on_the_air_and_the_field_uses_the_label()
    {
        string check = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"));
        Assert.DoesNotContain("\"Low power level\"", check);
        Assert.Contains("Setup(AudioCheckPowerRule.LowPowerLevelLabel", check);

        int plan = check.IndexOf("AudioCheckPowerRule.Plan(rig.RadioModel", StringComparison.Ordinal);
        int decide = check.IndexOf("AudioCheckPowerRule.CapLeavesPowerUnchanged(", StringComparison.Ordinal);
        int keyed = check.IndexOf("ScreenReaderOutput.Speak(\"Transmitting\"", StringComparison.Ordinal);
        int said = check.IndexOf("AudioCheckPowerRule.CapAboveCurrentNotice(", StringComparison.Ordinal);
        Assert.True(plan > 0 && decide > plan, "the no-change case is decided from the same inputs as the plan");
        Assert.True(keyed > 0 && said > keyed, "and said behind the on-air confirmation, so a refused key says nothing about power");
        Assert.Equal(said, check.LastIndexOf("AudioCheckPowerRule.CapAboveCurrentNotice(", StringComparison.Ordinal));
    }
}
