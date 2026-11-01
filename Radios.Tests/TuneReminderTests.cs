using System;
using System.IO;
using Xunit;

namespace Radios.Tests;

// LEXICON_SCANNER_EXEMPT: the pins below quote source lines, escaped quotes and
// all, so the scanner would read each as a call with a mangled key.

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

    [Fact]
    public void An_edge_between_ticks_starts_the_next_carrier_from_nothing()
    {
        Assert.Equal(0, RemindersOver(20));
        _clock.Reset();                        // off and on again between two ticks
        Assert.Equal(0, RemindersOver(25));    // no time carried over
        Assert.Equal(TuneReminderStep.Remind, Tick(true));
    }

    private static string Source(params string[] path) =>
        File.ReadAllText(Path.Combine(IntegrationPassTree.Root, Path.Combine(path)));

    [Fact]
    public void The_window_hands_the_radio_and_the_owner_to_the_reminder_and_never_writes_a_carrier()
    {
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        int at = main.IndexOf("private void TuneReminderTick()", StringComparison.Ordinal);
        string tick = main.Substring(at, main.IndexOf("/// Show or hide the meters panel.", at, StringComparison.Ordinal) - at);
        Assert.Contains("rig != null && rig.IsConnected && rig.TuneCarrierUp", tick);
        Assert.Contains("Radios.TransmitKillSwitch.HoldsTuneCarrier(rig!)", tick);
        Assert.DoesNotContain("TransmitKillSwitch.Armed", tick);
        Assert.DoesNotContain("TxTune =", tick);
        Assert.DoesNotContain("Transmit =", tick);
        Assert.DoesNotContain("DropCarrier", tick);
    }

    [Fact]
    public void It_says_what_was_ruled_under_its_own_subject()
    {
        string src = Source("JJFlexWpf", "TuneReminder.cs");
        Assert.Contains("EarconPlayer.Warning1Beep();", src);
        Assert.Contains("Lexicon.Get(\"audio.tune.still_on\")", src);
        Assert.Contains("subject: SpeechSubject.TuneStillOn", src);
    }

    [Theory]
    [InlineData("EndTuneAnswer(\"tune off\");")]
    [InlineData("EndTuneAnswer(\"the connection fell\");")]
    [InlineData("EndTuneAnswer(\"the radio's events were unwired\");")]
    [InlineData("EndTuneAnswer(\"the radio powered off\");")]
    [InlineData("if (!transmit) _tuneReminder.End(\"the radio going to receive\");")]
    [InlineData("_tuneReminder.End(\"the radio dropping tune\");")]
    [InlineData("RigControl.TuneCarrierFell += TuneCarrierFellHandler;")]
    public void Every_real_edge_ends_it_at_once(string line) =>
        Assert.Contains(line, Source("JJFlexWpf", "MainWindow.xaml.cs"));

    [Fact]
    public void Every_carrier_a_check_raises_needs_the_arm_the_owner_is_recorded_under()
    {
        string kill = Source("Radios", "TransmitKillSwitch.cs");
        int gate = kill.IndexOf("if (_arms == 0 || !ReferenceEquals(_rig, rig))", StringComparison.Ordinal);
        int owner = kill.IndexOf("if (carrier == Carrier.Tune) _tuneRaisedOn = rig;", StringComparison.Ordinal);
        Assert.True(gate > 0 && owner > gate);
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
