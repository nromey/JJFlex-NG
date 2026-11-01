using System;
using System.IO;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Ctrl+Enter in Check your audio starts a check only on a new press (#859).
/// The start is a counter here, so the whole run from a typed entry through a
/// held key to a second press can be walked with no window and no radio.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class FreshPressGateTests
{
    private const string Six = "FLEX-6300";
    private const string Eight = "FLEX-8600";

    private readonly FreshPressGate _gate = new();
    private long _now = 10_000;
    private int _starts;
    private int _settled;

    /// <summary>One key-down, as the window handles it: settle the field, then start if it goes on.</summary>
    private void Press(bool repeat = false, Func<bool> field = null, int after = 0)
    {
        _now += after;
        if (_gate.GoesOn(_now, repeat, () => { _settled++; return field?.Invoke() ?? true; })) _starts++;
    }

    /// <summary>The field's answer for a pending entry, by the real rule on the check's own field.</summary>
    private static Func<bool> Pending(string model, bool parsed, int typed)
    {
        bool done = false;
        return () =>
        {
            if (done) return true;      // the entry is finished: nothing pending the next time it is asked
            done = true;
            return ValueFieldRules.Judge(parsed, typed, 1, 100, t => AudioCheckPowerRule.TypedWattsRefusal(model, t),
                out _, out _) == TypedEntry.Taken;
        };
    }

    [Fact]
    public void A_tap_starts()
    {
        Press();
        Assert.Equal(1, _starts);
    }

    [Theory]
    [InlineData(Six, 5)]
    [InlineData(Eight, 5)]
    public void A_number_taken_as_typed_lets_the_same_press_start(string model, int typed)
    {
        Press(field: Pending(model, true, typed));
        Assert.Equal(1, _starts);
    }

    [Theory]
    [InlineData(Six, true, 0)]        // refused
    [InlineData(Eight, true, 0)]      // clamped to 1
    [InlineData(Eight, true, 101)]    // clamped to 100
    [InlineData(Six, true, 101)]
    [InlineData(Six, false, 0)]       // not a number
    [InlineData(Eight, false, 0)]
    public void A_number_not_taken_stops_the_press_and_every_repeat_of_it(string model, bool parsed, int typed)
    {
        var field = Pending(model, parsed, typed);
        Press(field: field);
        for (int i = 0; i < 20; i++) Press(repeat: true, field: field, after: i == 0 ? 500 : 31);
        Assert.Equal(0, _starts);
        Assert.Equal(1, _settled);
    }

    [Theory]
    [InlineData(Six, true, 0)]
    [InlineData(Eight, true, 0)]
    [InlineData(Eight, false, 0)]
    public void The_next_press_after_letting_go_starts(string model, bool parsed, int typed)
    {
        var field = Pending(model, parsed, typed);
        Press(field: field);
        _gate.NoteControlUp();
        Press(field: field, after: 2_000);
        Assert.Equal(1, _starts);       // the positive control: only the held press was held off
    }

    [Fact]
    public void A_refusal_also_outlasts_pairs_that_look_like_fresh_presses()
    {
        // The shape measured on a scripted key under JAWS: not flagged as
        // repeats, the first at the keyboard delay, then four a second.
        var field = Pending(Eight, true, 0);
        Press(field: field);
        Press(field: field, after: 512);
        for (int i = 0; i < 8; i++) Press(field: field, after: 250);
        Assert.Equal(0, _starts);
    }

    [Fact]
    public void A_refusal_outlasts_pairs_even_if_they_carry_the_control_key_too()
    {
        var field = Pending(Eight, true, 0);
        Press(field: field);
        for (int i = 0; i < 8; i++)
        {
            _gate.NoteControlUp();
            Press(field: field, after: i == 0 ? 512 : 250);
        }
        Assert.Equal(0, _starts);
    }

    [Fact]
    public void A_press_that_started_a_check_does_not_stop_it_by_being_held()
    {
        Press();
        for (int i = 0; i < 20; i++) Press(repeat: true, after: 31);
        Press(after: 512);
        Press(after: 250);
        Assert.Equal(1, _starts);       // one action for one press, on both shapes of a hold
    }

    [Fact]
    public void A_quick_second_tap_still_acts_because_stopping_must_stay_quick()
    {
        Press();
        _gate.NoteControlUp();
        Press(after: 200);
        Assert.Equal(2, _starts);
    }

    [Fact]
    public void A_control_up_the_window_never_saw_cannot_lock_the_key()
    {
        Press();                        // focus left the window with Control down
        Press(after: FreshPressGate.SameChordWindowMs + 1);
        Assert.Equal(2, _starts);
    }

    [Fact]
    public void The_window_goes_through_the_gate_and_hears_control_go_up()
    {
        string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.cs"));
        int key = src.IndexOf("Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter", StringComparison.Ordinal);
        int gate = src.IndexOf("_ctrlEnterPress.GoesOn(Environment.TickCount64, e.IsRepeat,", key, StringComparison.Ordinal);
        int toggle = src.IndexOf("ToggleAudioCheck();", key, StringComparison.Ordinal);
        int stop = src.IndexOf("StopCheckPlayback()", key, StringComparison.Ordinal);
        Assert.True(key > 0 && gate > key && gate < toggle && gate < stop);
        Assert.Contains("_ctrlEnterPress.NoteControlUp();", src);
        Assert.Contains("e.Key is Key.LeftCtrl or Key.RightCtrl", src);
    }
}
