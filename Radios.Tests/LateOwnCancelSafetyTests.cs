#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Radios.Speech;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// What the speech queue must keep doing after #1040's round 2 was blocked
/// (Codex, 2026-11-09, B1 and B2). NVDA's controller queues our cancel and
/// returns, so the cancel for an interrupt can be reported against the next
/// utterance handed over. Round 2 (<c>7672cee4</c>, reverted in <c>9d69a8e4</c>)
/// read any word-zero cut close in time to our own cancel as ours. That broke
/// two guarantees, which these pin from their created states, adapted from
/// Codex's review probes:
/// </summary>
/// <remarks>
/// <para><b>B1: a cut from outside always silences what is queued</b> (#182),
/// however close it falls to a cancel of ours.</para>
/// <para><b>B2: a cut before the first word is never counted as heard or
/// started</b> by a hearing gate, here the whole-notice gate in front of keying
/// (<see cref="TransmitSliceNotice.HeardAsync(ICountdownWords, Func{bool}, Func{int, System.Threading.Tasks.Task}, int, int)"/>).</para>
/// <para>Each test went red on round 2's code (run 2026-11-10 against
/// <c>7672cee4</c>'s speech files) and is green on the revert. Nothing here
/// speaks: the reader is <see cref="FakeCompletionChannel"/>.</para>
/// </remarks>
public sealed class LateOwnCancelSafetyTests
{
    private static PacedSpeechDelivery Pump(ConcurrentDictionary<long, SpeechOutcome> outcomes,
        Action<long, string, SpeechOutcome>? also = null) =>
        new((_, _) => SpeechDelivery.Accepted, (t, text, o) => { also?.Invoke(t, text, o); outcomes[t] = o; }, _ => 10000);

    /// <summary>B1, Codex's first probe: our interrupt has finished; the operator then cuts a new sentence before its first word.</summary>
    [Fact]
    public void B1_a_cut_from_outside_after_our_interrupt_has_finished_still_silences_the_queue()
    {
        var ch = new FakeCompletionChannel();
        var outcomes = new ConcurrentDictionary<long, SpeechOutcome>();
        using var pump = Pump(outcomes);
        try
        {
            long first = pump.Interrupt(ch, "Our interrupt, completed normally.");
            ch.Complete(ch.WaitForCall());
            Assert.True(SpinWait.SpinUntil(() => outcomes.ContainsKey(first), 2000));
            Assert.Equal(SpeechOutcomeKind.Completed, outcomes[first].Kind);   // positive control

            long a = pump.Enqueue(ch, "Now cut by the operator.");
            long b = pump.Enqueue(ch, "This must stay silent.");
            var call = ch.WaitForCall();
            Assert.Equal(a, call.Ticket);
            ch.CancelFromOutside(call, marksReached: 0, elapsedMs: 10);

            Assert.Throws<TimeoutException>(() => ch.WaitForCall(400));    // red on round 2: b reached the reader
            Assert.True(SpinWait.SpinUntil(() => outcomes.ContainsKey(a) && outcomes.ContainsKey(b), 2000));
            Assert.Equal(SpeechOutcomeKind.Cancelled, outcomes[a].Kind);
            Assert.False(outcomes[a].CancelledByUs);
            Assert.Equal(SpeechOutcomeKind.Cancelled, outcomes[b].Kind);
            Assert.DoesNotContain(ch.Seen, s => s.Ticket == b);
        }
        finally { ch.ReleaseAll(); }
    }

    /// <summary>
    /// B1, Codex's two-cut probe: an interrupt with two sentences behind it,
    /// the interrupter cut at word zero straight after our own cancel. Nothing
    /// behind it speaks, so one cancel of ours can never stand in for two cuts.
    /// </summary>
    [Fact]
    public void B1_a_word_zero_cut_right_after_our_own_cancel_still_silences_everything_behind_it()
    {
        var ch = new FakeCompletionChannel();
        var outcomes = new ConcurrentDictionary<long, SpeechOutcome>();
        using var pump = Pump(outcomes);
        try
        {
            long a = pump.Interrupt(ch, "First.");
            long b = pump.Enqueue(ch, "Second.");
            long c = pump.Enqueue(ch, "Third.");
            Assert.Equal(1, ch.CancelCalls);                                  // positive control: our cancel went out
            var first = ch.WaitForCall();
            Assert.Equal(a, first.Ticket);
            ch.CancelFromOutside(first, marksReached: 0, elapsedMs: 0);

            Assert.Throws<TimeoutException>(() => ch.WaitForCall(400));    // red on round 2: b reached the reader
            Assert.True(SpinWait.SpinUntil(() => outcomes.ContainsKey(b) && outcomes.ContainsKey(c), 2000));
            Assert.Equal(SpeechOutcomeKind.Cancelled, outcomes[a].Kind);
            Assert.DoesNotContain(ch.Seen, s => s.Ticket == b || s.Ticket == c);
        }
        finally { ch.ReleaseAll(); }
    }

    /// <summary>
    /// B2, Codex's paired probe, through the real pump, arbiter, countdown
    /// speech and whole-notice gate: a notice cut before its first word is
    /// never finished, never started, and the gate refuses, whether or not a
    /// cancel of ours went out just before it. No radio; nothing keys.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // red on round 2: the notice became Finished and the gate passed
    public void B2_a_whole_notice_cut_before_its_first_word_never_passes_the_gate(bool recentOwnCancel)
    {
        var ch = new FakeCompletionChannel();
        var clock = new FakeSpeechClock();
        var outcomes = new ConcurrentDictionary<long, SpeechOutcome>();
        SpeechArbiter? arbiter = null;
        using var pump = Pump(outcomes, (t, text, o) => arbiter?.OnOutcome(t, text, o));
        arbiter = new SpeechArbiter(clock, () => VerbosityLevel.Chatty,
            (text, interrupt, _, _, _, _) => SpeechHandoff.TrackedAs(interrupt ? pump.Interrupt(ch, text) : pump.Enqueue(ch, text)),
            () => { }, (_, _, _, _) => { });
        try
        {
            if (recentOwnCancel)
            {
                long first = pump.Interrupt(ch, "An earlier interrupt.");
                ch.Complete(ch.WaitForCall());
                Assert.True(SpinWait.SpinUntil(() => outcomes.ContainsKey(first), 2000));
            }
            var speech = new CountdownSpeech(arbiter, pump.HasReachedFirstWord, () => pump.NextProgress);
            var words = speech.SayWhole("A required sentence before the key.", "late-own-cancel-whole-notice");
            var call = ch.WaitForCall();
            ch.CancelFromOutside(call, marksReached: 0, elapsedMs: 0);
            Assert.True(SpinWait.SpinUntil(() => outcomes.ContainsKey(call.Ticket), 2000));
            Assert.False(outcomes[call.Ticket].WasHeard);
            Assert.Equal(SpeechOutcomeKind.Cancelled, outcomes[call.Ticket].Kind);

            clock.Advance(10000);
            Assert.True(words.CutBeforeItsFirstWord);
            Assert.False(words.Started);
            Assert.False(words.Finished);
            bool gate = TransmitSliceNotice.HeardAsync(words, () => true,
                _ => System.Threading.Tasks.Task.CompletedTask, ceilingMs: 100, queuedCeilingMs: 100)
                .GetAwaiter().GetResult();
            Assert.False(gate);
        }
        finally { ch.ReleaseAll(); }
    }
}
