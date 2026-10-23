#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests;

public sealed class VoxSafetyFollowupTests
{
    private const string Cut = "Transmit stopped. Check the antenna before keying again.";
    private const string Note = "VOX was paused for the hold on keying.";
    private readonly FakeSpeechClock _clock = new();
    private readonly List<(long Ticket, string Text)> _delivered = new();
    private readonly SpeechArbiter _arbiter;
    private long _ticket;
    private readonly List<bool> _interrupts = new();

    public VoxSafetyFollowupTests()
    {
        _arbiter = new SpeechArbiter(_clock, () => VerbosityLevel.Chatty,
            (text, interrupt, intent, level, origin, salvaged) =>
            {
                _delivered.Add((++_ticket, text));
                _interrupts.Add(interrupt);
                return SpeechHandoff.TrackedAs(_ticket);
            }, () => { }, (text, level, intent, origin) => { });
    }

    [Fact]
    public void Note_waits_for_cut_then_recovers_after_an_unsolicited_zero_word_cancel()
    {
        _arbiter.Urgent(Cut, VerbosityLevel.Critical, "test", SpeechSubject.ReflectedPowerCut);
        _arbiter.SafetyFollowup(Note, VerbosityLevel.Terse, "test", SpeechSubject.VoxPausedAfterCut);
        Assert.Single(_delivered);
        Answer(_delivered[0], completed: true);
        _clock.Advance(1000);
        Assert.Equal(Note, _delivered.Last().Text);
        Assert.False(_interrupts.Last());
        var firstNote = _delivered.Last();
        Answer(firstNote, completed: false);
        // The existing protected recovery contract retries on the next
        // ordinary handover; a callback alone is not permission to speak.
        _arbiter.Emit("Frequency 14.250.", true, SpeechIntent.Interrupt,
            VerbosityLevel.Terse, "test", "frequency");
        _clock.Advance(2000);
        Assert.Equal(2, _delivered.Count(d => d.Text == Note));
        Assert.True(_interrupts.Last());
        Answer(_delivered.Last(), completed: true);
        Assert.Empty(_arbiter.OwedSafetyObligations);
    }

    [Fact]
    public void A_refused_press_cannot_discard_the_waiting_vox_note()
    {
        _arbiter.Urgent(Cut, VerbosityLevel.Critical, "test", SpeechSubject.ReflectedPowerCut);
        _arbiter.SafetyFollowup(Note, VerbosityLevel.Terse, "test", SpeechSubject.VoxPausedAfterCut);
        _arbiter.Urgent("Unable to key the transmitter.", VerbosityLevel.Critical, "test", SpeechSubject.ReflectedPowerCut);
        Answer(_delivered.Last(), completed: true);
        _clock.Advance(1000);
        Assert.Equal(Note, _delivered.Last().Text);
    }

    [Fact]
    public void Teardown_withdraws_a_note_waiting_for_the_cut()
    {
        bool valid = true;
        _arbiter.Urgent(Cut, VerbosityLevel.Critical, "test", SpeechSubject.ReflectedPowerCut);
        _arbiter.SafetyFollowup(Note, VerbosityLevel.Terse, "test", SpeechSubject.VoxPausedAfterCut, () => valid);
        valid = false;
        Answer(_delivered[0], completed: true);
        _clock.Advance(1000);
        Assert.DoesNotContain(_delivered, d => d.Text == Note);
        Assert.Empty(_arbiter.OwedSafetyObligations);
    }

    private void Answer((long Ticket, string Text) call, bool completed)
    {
        int words = NvdaCompletionChannel.SplitWords(call.Text).Length;
        _arbiter.OnOutcome(call.Ticket, call.Text, completed
            ? SpeechOutcome.Completed(words, words, 100)
            : SpeechOutcome.Cancelled(0, words, byUs: false, 14));
    }
}
