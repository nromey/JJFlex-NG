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
    private int _stops;
    private int _settled;
    private bool _live;

    /// <summary>One key-down, as the window handles it. A start makes the check live and a stop ends it, as the real one does.</summary>
    private void Press(bool repeat = false, Func<bool> field = null, int after = 0, bool? live = null)
    {
        _now += after;
        switch (_gate.Decide(_now, repeat, live ?? false, () => { _settled++; return field?.Invoke() ?? true; }))
        {
            case PressAct.Start: _starts++; break;
            case PressAct.Stop: _stops++; break;
        }
    }

    /// <summary>The same, with the check's own life: what a start began, a stop ended.</summary>
    private void PressLive(bool repeat = false, int after = 0)
    {
        _now += after;
        switch (_gate.Decide(_now, repeat, _live, () => true))
        {
            case PressAct.Start: _starts++; _live = true; break;
            case PressAct.Stop: _stops++; _live = false; break;
        }
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
    public void A_held_key_flagged_as_repeats_starts_once_and_stops_nothing()
    {
        PressLive();
        for (int i = 0; i < 20; i++) PressLive(repeat: true, after: 31);
        Assert.Equal(1, _starts);
        Assert.Equal(0, _stops);
    }

    // ── A stop is never swallowed ───────────────────────────────────────

    [Fact]
    public void A_second_tap_with_Control_held_stops_the_check()
    {
        PressLive();                    // starts
        PressLive(after: 200);          // Enter up and down again, Control never released
        Assert.Equal(1, _stops);
        Assert.False(_live);
    }

    [Fact]
    public void A_pending_start_is_cancelled_the_same_way()
    {
        Press();                        // the start: the count is playing, nothing keyed yet
        Press(after: 200, live: true);
        Assert.Equal(1, _stops);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void A_stop_goes_whatever_the_time(int after)
    {
        PressLive();
        PressLive(after: after);
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void A_stop_goes_even_right_after_a_refusal()
    {
        Press(field: () => false);      // refused, and the window is holding
        Press(after: 100, live: true);  // something else went live; a stop is a stop
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void A_stop_goes_whatever_the_field_says_about_a_pending_number()
    {
        Press(field: () => false, live: true);
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void A_repeat_never_stops()
    {
        PressLive();
        PressLive(repeat: true, after: 500);
        Assert.Equal(0, _stops);
        Assert.True(_live);
    }

    [Fact]
    public void Pairs_stop_the_check_they_started_and_then_start_nothing_which_is_the_safe_direction()
    {
        PressLive();                    // down
        PressLive(after: 512);          // the first pair looks like a press: it stops
        Assert.Equal(1, _stops);
        for (int i = 0; i < 8; i++) PressLive(after: 250);
        Assert.Equal(1, _starts);       // no flip-flop: the rest are the same press, and start nothing
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void After_a_stop_a_held_key_cannot_start_again()
    {
        PressLive();
        PressLive(after: 200);
        PressLive(repeat: true, after: 31);
        PressLive(after: 200);          // Control still held, no longer live
        Assert.Equal(1, _starts);
    }

    [Fact]
    public void A_repeat_is_a_repeat_however_late_it_arrives()
    {
        Press();
        _gate.NoteControlUp();
        Press(repeat: true, after: FreshPressGate.SameChordWindowMs * 3);   // a busy window delivered it late
        Assert.Equal(1, _starts);
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
        int gate = src.IndexOf("_ctrlEnterPress.Decide(Environment.TickCount64, e.IsRepeat, checkLive || CheckPlaybackRunning,", key, StringComparison.Ordinal);
        int stopCase = src.IndexOf("case PressAct.Stop:", key, StringComparison.Ordinal);
        int cancel = src.IndexOf("if (checkLive) ToggleAudioCheck();", key, StringComparison.Ordinal);
        int startCase = src.IndexOf("case PressAct.Start:", key, StringComparison.Ordinal);
        Assert.True(key > 0 && gate > key && stopCase > gate && cancel > stopCase && startCase > cancel);
        // Everything ToggleAudioCheck stops counts as live.
        Assert.Contains("bool checkLive = _checkStarts.IsBusy || _iqBusy || _session?.Active == true || ComputerMicCheckRunning;", src);
        Assert.Contains("_ctrlEnterPress.NoteControlUp();", src);
        Assert.Contains("e.Key is Key.LeftCtrl or Key.RightCtrl", src);
    }

    // ── A door's refusal (a press that would key a rig at 0 watts) ──────

    [Fact]
    public void A_refusal_is_answered_once_across_a_held_key_delivered_as_pairs()
    {
        Assert.True(_gate.NewRefusal(_now));
        _now += 512;                                 // the keyboard's repeat delay
        Assert.False(_gate.NewRefusal(_now));
        for (int i = 0; i < 20; i++)
        {
            _now += 250;                             // a pair about every 250 ms: each looks like a new press
            Assert.False(_gate.NewRefusal(_now));
        }
    }

    [Fact]
    public void Each_repeat_renews_the_window_so_a_long_hold_never_answers_again()
    {
        _gate.NewRefusal(_now);
        for (int i = 0; i < 10; i++)
        {
            _now += FreshPressGate.SameChordWindowMs - 1;
            Assert.False(_gate.NewRefusal(_now));
        }
    }

    [Fact]
    public void A_new_press_after_the_window_is_answered()
    {
        Assert.True(_gate.NewRefusal(_now));
        _now += FreshPressGate.SameChordWindowMs;
        Assert.True(_gate.NewRefusal(_now));         // the positive control
    }
}
