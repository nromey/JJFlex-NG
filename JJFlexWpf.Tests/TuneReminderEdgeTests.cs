using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Tune off between two reminder ticks (#860): the sentence is retired by the
/// production call at that moment, not by the next tick and not by a test
/// calling Supersede for it.
/// </summary>
/// <remarks>
/// Windowless: an MTA thread and an UNINITIALIZED main window (no constructor,
/// no XAML, no HWND), holding a real <see cref="TuneReminder"/>.
/// </remarks>
public sealed class TuneReminderEdgeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly List<(string Subject, string Why)> _retired = new();

    public TuneReminderEdgeTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Lexicon.Load(Lexicon.Partitions);
    }

    private TuneReminder Reminder() => new((subject, why) => _retired.Add((subject, why)));

    private static void UpFor(TuneReminder reminder, ref long now, int seconds)
    {
        for (int i = 0; i <= seconds; i++, now += 1000) reminder.Tick(true, false, now, 25);
    }

    [Fact]
    public void The_window_retires_it_the_moment_tune_goes_off()
    {
        var reminder = Reminder();
        long now = 1_000;
        UpFor(reminder, ref now, 25);       // the reminder has just gone out
        Assert.Empty(_retired);

        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        typeof(MainWindow).GetField("_tuneReminder", Private)!.SetValue(window, reminder);
        typeof(MainWindow).GetMethod("EndTuneAnswer", Private)!.Invoke(window, new object[] { "tune off" });

        // Before any further tick.
        Assert.Equal(new[] { (SpeechSubject.TuneStillOn, "tune off") }, _retired);
    }

    private MainWindow WindowHolding(TuneReminder reminder)
    {
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        typeof(MainWindow).GetField("_tuneReminder", Private)!.SetValue(window, reminder);
        return window;
    }

    [Theory]
    [InlineData("TuneCarrierFellHandler", false)]
    [InlineData("TuneReminderTransmitEdge", true)]
    public void A_radio_side_edge_retires_it_on_its_own_thread_with_the_dispatcher_doing_nothing(string handler, bool transmitEvent)
    {
        var reminder = Reminder();
        long now = 1_000;
        UpFor(reminder, ref now, 25);       // said, and could be held for recovery
        var window = WindowHolding(reminder);
        var method = typeof(MainWindow).GetMethod(handler, Private)!;

        // A worker thread, and an uninitialized window has no dispatcher to
        // post to: only a retirement made right here can land.
        Task.Run(() => method.Invoke(window, transmitEvent ? new object?[] { window, false } : null)).GetAwaiter().GetResult();

        Assert.Single(_retired);
        Assert.Equal(SpeechSubject.TuneStillOn, _retired[0].Subject);
    }

    [Fact]
    public void Going_to_transmit_retires_nothing()
    {
        var reminder = Reminder();
        var window = WindowHolding(reminder);
        typeof(MainWindow).GetMethod("TuneReminderTransmitEdge", Private)!.Invoke(window, new object?[] { window, true });
        Assert.Empty(_retired);
    }

    [Fact]
    public void An_edge_that_lands_between_the_clock_and_the_sentence_retires_it_after_the_sentence()
    {
        TuneReminder? reminder = null;
        var order = new List<string>();
        reminder = new TuneReminder((_, why) => order.Add("retired: " + why), () =>
        {
            order.Add("said");
            reminder!.End("tune off");      // no earlier than the speech call, no later than its return
        });
        long now = 1_000;
        UpFor(reminder, ref now, 25);
        Assert.Equal(new[] { "said", "retired: tune off", "retired: the tune carrier going off as it was said" }, order);
    }

    [Fact]
    public void A_tick_after_the_edge_has_nothing_left_to_retire()
    {
        var reminder = Reminder();
        long now = 1_000;
        UpFor(reminder, ref now, 5);
        reminder.End("tune off");
        _retired.Clear();
        reminder.Tick(false, false, now, 25);
        Assert.Empty(_retired);
    }

    [Fact]
    public void Off_and_on_again_between_ticks_does_not_inherit_the_old_time()
    {
        var reminder = Reminder();
        long now = 1_000;
        UpFor(reminder, ref now, 20);
        reminder.End("tune off");
        string spoken = Lexicon.Get("audio.tune.still_on");
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        UpFor(reminder, ref now, 20);       // 20 + 20 would have passed 25
        Assert.NotEqual(spoken, System.Linq.Enumerable.FirstOrDefault(ScreenReaderOutput.RecentMessages));
    }

    [Fact]
    public void The_ticks_retire_it_too_for_an_edge_nobody_announced()
    {
        var reminder = Reminder();
        long now = 1_000;
        UpFor(reminder, ref now, 5);
        reminder.Tick(false, false, now, 25);
        Assert.Equal(new[] { (SpeechSubject.TuneStillOn, "the tune carrier going off") }, _retired);
    }
}
