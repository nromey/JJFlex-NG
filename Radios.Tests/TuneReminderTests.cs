using System;
using System.IO;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The tune reminder (#860): after the set length of a carrier being up, and
/// again at that length while it stays up. Speech and a tone only.
/// </summary>
public sealed class TuneReminderTests
{
    private readonly TuneReminderClock _clock = new();
    private long _now = 1_000;

    private TuneReminderStep Tick(bool up, int seconds = 25, bool exempt = false)
    {
        var step = _clock.Tick(up, exempt, _now, seconds);
        _now += 1000;
        return step;
    }

    private int RemindersOver(int ticks, int seconds = 25)
    {
        int count = 0;
        for (int i = 0; i < ticks; i++) if (Tick(true, seconds) == TuneReminderStep.Remind) count++;
        return count;
    }

    [Fact]
    public void A_short_tune_never_hears_it()
    {
        Assert.Equal(0, RemindersOver(25));
        Assert.Equal(TuneReminderStep.Over, Tick(false));
    }

    [Fact]
    public void It_comes_after_the_set_length_and_not_before()
    {
        Assert.Equal(0, RemindersOver(25));
        Assert.Equal(TuneReminderStep.Remind, Tick(true));
    }

    [Fact]
    public void It_repeats_at_the_same_length_while_the_carrier_stays_up()
    {
        Assert.Equal(TuneReminderStep.Nothing, Tick(true));
        Assert.Equal(3, RemindersOver(75));
    }

    [Fact]
    public void A_tube_amplifiers_long_setting_holds_its_peace()
    {
        Assert.Equal(0, RemindersOver(599, 600));
        Assert.Equal(1, RemindersOver(5, 600));
    }

    [Fact]
    public void A_change_in_Settings_takes_effect_on_the_carrier_in_hand()
    {
        Assert.Equal(0, RemindersOver(20, 25));
        Assert.Equal(TuneReminderStep.Remind, Tick(true, 10));
    }

    [Fact]
    public void Tune_going_off_ends_it_once_and_the_next_tune_starts_from_nothing()
    {
        Assert.Equal(0, RemindersOver(20));
        Assert.Equal(TuneReminderStep.Over, Tick(false));
        Assert.Equal(TuneReminderStep.Nothing, Tick(false));   // said once
        Assert.Equal(0, RemindersOver(25));
        Assert.Equal(TuneReminderStep.Remind, Tick(true));
    }

    [Fact]
    public void A_carrier_a_check_holds_does_not_hear_it()
    {
        for (int i = 0; i < 100; i++) Assert.Equal(TuneReminderStep.Nothing, Tick(true, exempt: true));
    }

    [Fact]
    public void A_carrier_left_up_after_a_check_starts_the_clock_then()
    {
        for (int i = 0; i < 60; i++) Tick(true, exempt: true);
        Assert.Equal(0, RemindersOver(25));
        Assert.Equal(TuneReminderStep.Remind, Tick(true));
    }

    [Fact]
    public void A_carrier_a_check_takes_over_retires_what_was_being_said()
    {
        Tick(true);
        Assert.Equal(TuneReminderStep.Over, Tick(true, exempt: true));
    }

    private static string Source(params string[] path) =>
        File.ReadAllText(Path.Combine(IntegrationPassTree.Root, Path.Combine(path)));

    private static string Tick_()
    {
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        int at = main.IndexOf("private void TuneReminderTick()", StringComparison.Ordinal);
        Assert.True(at > 0);
        return main.Substring(at, main.IndexOf("/// Show or hide the meters panel.", at, StringComparison.Ordinal) - at);
    }

    [Fact]
    public void The_window_asks_the_radio_and_the_kill_switch_and_never_writes_a_carrier()
    {
        string tick = Tick_();
        Assert.Contains("rig.TuneCarrierUp", tick);
        Assert.Contains("Radios.TransmitKillSwitch.Armed", tick);
        Assert.DoesNotContain("TxTune =", tick);
        Assert.DoesNotContain("Transmit =", tick);
        Assert.DoesNotContain("DropCarrier", tick);
    }

    [Fact]
    public void It_says_what_was_ruled_under_its_own_subject_and_retires_it()
    {
        string tick = Tick_();
        Assert.Contains("EarconPlayer.Warning1Beep();", tick);
        Assert.Contains("Radios.Lexicon.Get(\"audio.tune.still_on\")", tick);
        Assert.Contains("subject: Radios.Speech.SpeechSubject.TuneStillOn", tick);
        Assert.Contains("Radios.ScreenReaderOutput.Supersede(Radios.Speech.SpeechSubject.TuneStillOn,", tick);
    }

    [Fact]
    public void The_timer_is_its_own_because_the_status_poll_stops_with_the_connection()
    {
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        Assert.Contains("StartTuneReminder();", main);
        // The reading treats a lost radio as down, which is what ends the clock.
        Assert.Contains("rig != null && rig.IsConnected && rig.TuneCarrierUp", Tick_());
    }

    [Fact]
    public void Every_carrier_a_check_raises_needs_the_arm_the_exemption_reads()
    {
        string kill = Source("Radios", "TransmitKillSwitch.cs");
        Assert.Contains("if (_arms == 0 || !ReferenceEquals(_rig, rig))", kill);
    }

    [Fact]
    public void The_settings_page_loads_validates_and_saves_it_on_the_one_commit_path()
    {
        string settings = Source("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
        Assert.Contains("PttTuneReminderBox.Text = _pttConfig.TuneReminderSeconds.ToString();", settings);
        Assert.Contains("_pttConfig.TuneReminderSeconds = tuneReminder;", settings);
        Assert.Contains("JJFlexHelp.SetText(PttTuneReminderBox,", settings);
        Assert.Contains("x:Name=\"PttTuneReminderBox\"", Source("JJFlexWpf", "Dialogs", "SettingsDialog.xaml"));
    }
}
